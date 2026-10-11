#!/usr/bin/env python3
"""Mint a short-lived access token for the load test (SCRUM-133).

    AUTH_SIGNING_KEY=<the staging signing key> python3 scripts/mint-perf-token.py

Prints a signed JWT on stdout. It is built like the Auth service builds one (HS256, the same issuer,
audience and claim names), so Traceability accepts it exactly as it accepts a real sign-in, and it
carries the QualityControlOfficer role that /api/batches needs. Auth tokens last 60 minutes, so a
stored token cannot be used; the workflow mints a fresh one before every run.

Standard library only. The signing key comes from the environment, never from an argument or a file.
"""
import base64
import hashlib
import hmac
import json
import os
import sys
import time
import uuid


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def main() -> int:
    key = os.environ.get("AUTH_SIGNING_KEY", "")
    if len(key.encode("utf-8")) < 32:
        print("AUTH_SIGNING_KEY is missing or shorter than 32 bytes", file=sys.stderr)
        return 2

    minutes = int(os.environ.get("PERF_TOKEN_MINUTES", "30"))
    now = int(time.time())
    header = {"alg": "HS256", "typ": "JWT"}
    payload = {
        "sub": "perf-test",
        "jti": str(uuid.uuid4()),
        # The short claim names JwtSecurityTokenHandler writes for ClaimTypes.NameIdentifier, Name and Role.
        "nameid": "perf-test",
        "unique_name": "perf-test",
        "role": os.environ.get("PERF_TOKEN_ROLE", "QualityControlOfficer"),
        # A minute in the past, so a runner whose clock runs a little fast is not rejected (the service
        # validates with no clock skew, like the Auth service).
        "nbf": now - 60,
        "exp": now + minutes * 60,
        "iss": os.environ.get("AUTH_ISSUER", "wonrich-auth"),
        "aud": os.environ.get("AUTH_AUDIENCE", "wonrich-services"),
    }
    signing_input = (
        b64url(json.dumps(header, separators=(",", ":")).encode())
        + "."
        + b64url(json.dumps(payload, separators=(",", ":")).encode())
    )
    signature = hmac.new(key.encode("utf-8"), signing_input.encode("ascii"), hashlib.sha256).digest()
    print(signing_input + "." + b64url(signature))
    return 0


if __name__ == "__main__":
    sys.exit(main())
