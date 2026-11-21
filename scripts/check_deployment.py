"""Read-only deployment check. Never requests customer records or uses credentials."""
import argparse
import json
import time
import urllib.error
import urllib.request


def check(base_url, expected_revision):
    passed = True
    for path, expected_status in (("/health", "Core is running"), ("/ready", "ready")):
        try:
            with urllib.request.urlopen(base_url.rstrip("/") + path, timeout=8) as response:
                payload = json.load(response)
                ok = response.status == 200 and payload.get("status") == expected_status
                if path == "/health":
                    revision = payload.get("revision", "unknown")
                    if expected_revision:
                        ok = ok and revision == expected_revision
                    # Log only the expected comparison, not arbitrary response content.
                    detail = ("; revision matches" if revision == expected_revision else
                              "; revision mismatch") if expected_revision else (
                                  "; revision reported" if revision != "unknown" else "; revision not reported")
                else:
                    detail = ""
                print(f"{path}: HTTP {response.status}; {'PASS' if ok else 'FAIL'}{detail}", flush=True)
                passed = passed and ok
        except urllib.error.HTTPError as exc:
            print(f"{path}: HTTP {exc.code}; FAIL", flush=True)
            passed = False
        except (OSError, ValueError, AttributeError) as exc:
            print(f"{path}: {type(exc).__name__}; FAIL", flush=True)
            passed = False
    return passed


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="https://chatplatform.fly.dev")
    parser.add_argument("--expected-revision")
    parser.add_argument("--attempts", type=int, default=1)
    args = parser.parse_args()
    for attempt in range(max(1, args.attempts)):
        if check(args.base_url, args.expected_revision):
            print("PASS: health and database readiness confirmed.", flush=True)
            raise SystemExit(0)
        if attempt + 1 < args.attempts:
            time.sleep(5)
    raise SystemExit(1)
