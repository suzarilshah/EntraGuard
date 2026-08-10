"""
Speaker embeddings for EntraGuard.

This service does exactly two things: turn a chunk of speech into an embedding, and compare
two embeddings. It holds no state, stores nothing, and makes no decisions — whether a score
is good enough to grant access is decided in the media service, where the policy lives and
where it can be audited.

That split is deliberate. A model that both scores and decides is a model that can be
argued into deciding wrongly; here it cannot decide at all.

Audio arrives as raw 16-bit PCM at 16 kHz mono, which is what ECAPA-TDNN expects. It is
never written to disk, never logged, and the buffer is released as soon as the embedding
exists.
"""

import logging
import os
from contextlib import asynccontextmanager

import numpy as np
import torch
from fastapi import FastAPI, HTTPException, Request
from pydantic import BaseModel, Field

logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s: %(message)s")
log = logging.getLogger("voiceprint")

# Baked into the image at build time (see Dockerfile), so startup does not depend on
# HuggingFace being reachable and a cold replica does not stall a live call downloading
# eighty megabytes of model.
MODEL_SOURCE = os.environ.get("MODEL_SOURCE", "speechbrain/spkrec-ecapa-voxceleb")
MODEL_DIR = os.environ.get("MODEL_DIR", "/models/ecapa")

SAMPLE_RATE = 16000

# Below this there is not enough voiced audio for an embedding to mean anything. ECAPA will
# happily return a vector for 200 ms of breath; it just will not be a useful one, and a
# confident-looking number derived from nothing is worse than an honest refusal.
MIN_SAMPLES = int(1.0 * SAMPLE_RATE)

_encoder = None


@asynccontextmanager
async def lifespan(_: FastAPI):
    """Load the model once, at startup, so the first real request is not the slow one."""
    global _encoder
    from speechbrain.inference.speaker import EncoderClassifier

    log.info("Loading %s from %s", MODEL_SOURCE, MODEL_DIR)
    _encoder = EncoderClassifier.from_hparams(
        source=MODEL_DIR if os.path.isdir(MODEL_DIR) else MODEL_SOURCE,
        savedir=MODEL_DIR,
        run_opts={"device": "cpu"},
    )
    torch.set_num_threads(max(1, (os.cpu_count() or 2) - 1))
    log.info("Model ready")
    yield
    _encoder = None


app = FastAPI(title="EntraGuard voiceprint", lifespan=lifespan)


class ScoreRequest(BaseModel):
    a: list[float] = Field(..., min_length=8)
    b: list[float] = Field(..., min_length=8)


class EmbedResponse(BaseModel):
    embedding: list[float]
    dimensions: int
    seconds: float


@app.get("/health")
def health() -> dict:
    return {"status": "ready" if _encoder is not None else "loading"}


@app.post("/embed", response_model=EmbedResponse)
async def embed(request: Request) -> EmbedResponse:
    """
    Raw 16-bit little-endian PCM, 16 kHz mono, in the request body.

    Sent as bytes rather than JSON numbers because base64-in-JSON triples the payload for
    audio, and this runs while somebody is holding a phone.
    """
    if _encoder is None:
        raise HTTPException(status_code=503, detail="Model is still loading.")

    pcm = await request.body()

    if len(pcm) < MIN_SAMPLES * 2:
        raise HTTPException(
            status_code=422,
            detail=f"Need at least {MIN_SAMPLES / SAMPLE_RATE:.1f}s of audio; "
                   f"got {len(pcm) / 2 / SAMPLE_RATE:.2f}s.",
        )

    if len(pcm) % 2:
        pcm = pcm[:-1]

    samples = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0

    # Peak-normalise. Telephony levels vary enormously between handsets and the model was
    # trained on comparatively level audio; without this, quiet callers score lower than
    # loud ones for reasons that have nothing to do with who they are.
    peak = float(np.max(np.abs(samples))) if samples.size else 0.0
    if peak > 1e-4:
        samples = samples / peak

    with torch.no_grad():
        wav = torch.from_numpy(samples).unsqueeze(0)
        vector = _encoder.encode_batch(wav).squeeze().cpu().numpy().astype(np.float64)

    # L2-normalised here, so a cosine comparison downstream is a plain dot product and
    # every stored template is on the same scale regardless of how it was produced.
    norm = float(np.linalg.norm(vector))
    if norm < 1e-9:
        raise HTTPException(status_code=422, detail="Audio produced a degenerate embedding.")

    vector = vector / norm

    del samples, pcm
    return EmbedResponse(
        embedding=vector.tolist(),
        dimensions=int(vector.shape[0]),
        seconds=round(len(wav[0]) / SAMPLE_RATE, 3),
    )


@app.post("/score")
def score(request: ScoreRequest) -> dict:
    """Cosine similarity between two embeddings, in [-1, 1]."""
    a = np.asarray(request.a, dtype=np.float64)
    b = np.asarray(request.b, dtype=np.float64)

    if a.shape != b.shape:
        raise HTTPException(
            status_code=422,
            detail=f"Embeddings differ in size ({a.shape[0]} vs {b.shape[0]}); "
                   "they were produced by different models.",
        )

    na, nb = float(np.linalg.norm(a)), float(np.linalg.norm(b))
    if na < 1e-9 or nb < 1e-9:
        raise HTTPException(status_code=422, detail="Degenerate embedding.")

    return {"score": round(float(np.dot(a, b) / (na * nb)), 6)}


# Kept for the enrollment path, which needs to know whether three utterances of the same
# person actually agree with each other before it trusts their average as a template.
@app.post("/consistency")
def consistency(embeddings: list[list[float]]) -> dict:
    if len(embeddings) < 2:
        raise HTTPException(status_code=422, detail="Need at least two embeddings.")

    vectors = [np.asarray(e, dtype=np.float64) for e in embeddings]
    if len({v.shape for v in vectors}) != 1:
        raise HTTPException(status_code=422, detail="Embeddings differ in size.")

    scores = [
        float(np.dot(vectors[i], vectors[j]) /
              (np.linalg.norm(vectors[i]) * np.linalg.norm(vectors[j])))
        for i in range(len(vectors))
        for j in range(i + 1, len(vectors))
    ]

    mean = np.mean(vectors, axis=0)
    mean = mean / max(float(np.linalg.norm(mean)), 1e-9)

    return {
        "min_pairwise": round(min(scores), 6),
        "mean_pairwise": round(float(np.mean(scores)), 6),
        "template": mean.tolist(),
        "dimensions": int(mean.shape[0]),
    }

