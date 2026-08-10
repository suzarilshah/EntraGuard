# Build context is src/voiceprint (see scripts/deploy-apps.sh), unlike the media service
# which builds from the repo root.
FROM python:3.12-slim

# CPU-only torch, from PyTorch's own index.
#
# The default PyPI wheel drags in the entire CUDA runtime — roughly 6 GB of GPU libraries
# that will never execute on a Container Apps consumption replica. Pinning the CPU index
# takes the image to around 1.5 GB, which matters because ACR here is the Basic SKU with
# 10 GiB of included storage and because pull time is startup time.
RUN pip install --no-cache-dir \
      --index-url https://download.pytorch.org/whl/cpu \
      torch==2.5.1 torchaudio==2.5.1

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Fetch the model AT BUILD TIME.
#
# SpeechBrain downloads from HuggingFace on first use by default. That would put an
# eighty-megabyte download on the critical path of somebody's first verification call, make
# startup depend on huggingface.co being reachable, and mean two replicas could silently
# hold different model revisions. Baking it in makes the image the unit of versioning.
#
# HF_HOME lives inside /models on purpose. SpeechBrain does not copy fetched files into
# savedir — it SYMLINKS them into the HuggingFace cache, which defaults to /root/.cache and
# is unreadable by a non-root user. The image built fine and then died at startup with
# PermissionError on hyperparams.yaml, because the file the symlink pointed at sat behind
# root's home directory. Keeping the cache under /models means one chown covers both.
ENV MODEL_DIR=/models/ecapa \
    HF_HOME=/models/hf
COPY fetch_model.py .
RUN python fetch_model.py

COPY app.py .

# Same non-root UID as the other two images in this deployment.
RUN useradd --uid 1001 --create-home voiceprint \
 && chown -R 1001 /models \
 && chmod -R a+rX /models
USER 1001

EXPOSE 8000

# One worker on purpose. The model is several hundred megabytes resident, and a second
# worker doubles that for throughput this service will never need — enrollment happens once
# per user and verification scores a handful of seconds of audio per call.
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8000", "--workers", "1"]
