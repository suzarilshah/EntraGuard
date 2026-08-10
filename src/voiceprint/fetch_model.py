"""
Download the speaker model into the image at build time.

A separate file rather than an inline `python -c` in the Dockerfile: ACR's dependency
scanner parses RUN lines and fails outright on a backslash-continued Python one-liner.
"""

from speechbrain.inference.speaker import EncoderClassifier

EncoderClassifier.from_hparams(
    source="speechbrain/spkrec-ecapa-voxceleb",
    savedir="/models/ecapa",
    run_opts={"device": "cpu"},
)
print("model cached in /models/ecapa")
