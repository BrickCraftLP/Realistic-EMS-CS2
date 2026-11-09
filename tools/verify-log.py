# usage: python verify-log.py <export-folder> [public.pem]
import sys, json, hashlib, base64
from cryptography.hazmat.primitives.asymmetric import ec, utils
from cryptography.hazmat.primitives import hashes, serialization as s
d = sys.argv[1]; pem = sys.argv[2] if len(sys.argv) > 2 else "public.pem"
sig = json.load(open(d + "/RealisticEMS.log.sig.json"))
h = hashlib.sha256(open(d + "/RealisticEMS.log", "rb").read()).hexdigest()
if h != sig["sha256"]: sys.exit("FAIL: log was modified")
raw = base64.b64decode(sig["signature"])
der = utils.encode_dss_signature(int.from_bytes(raw[:32], "big"), int.from_bytes(raw[32:], "big"))
msg = f'{sig["version"]}|{sig["timestamp"]}|{sig["sha256"]}'.encode()
try: s.load_pem_public_key(open(pem, "rb").read()).verify(der, msg, ec.ECDSA(hashes.SHA256()))
except Exception: sys.exit("FAIL: bad signature")
print("OK")
