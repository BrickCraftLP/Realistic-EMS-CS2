# One-off: creates the signing key pair. Paste D/X/Y into RealisticEMS/LogExport.cs; keep public.pem. Never commit private.txt.
import base64
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives import serialization as s
k = ec.generate_private_key(ec.SECP256R1())
n = k.private_numbers(); b = lambda v: base64.b64encode(v.to_bytes(32, "big")).decode()
open("private.txt", "w").write(f'D = "{b(n.private_value)}"\nX = "{b(n.public_numbers.x)}"\nY = "{b(n.public_numbers.y)}"\n')
open("public.pem", "wb").write(k.public_key().public_bytes(s.Encoding.PEM, s.PublicFormat.SubjectPublicKeyInfo))
