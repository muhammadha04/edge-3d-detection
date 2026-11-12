"""QD3D Annotate PC tooling: read recorded sessions, project ground truth, export datasets, run and evaluate models."""
__version__ = "2.2.8"

import sys as _sys

# Windows consoles (cp1252) cannot print σ, Δ, °: never crash a report on that.
for _stream in (_sys.stdout, _sys.stderr):
    try:
        _stream.reconfigure(errors="replace")
    except (AttributeError, ValueError):
        pass
