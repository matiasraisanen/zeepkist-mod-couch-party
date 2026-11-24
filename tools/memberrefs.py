#!/usr/bin/env python3
"""List the fields and methods CouchParty.dll references directly in the game's Zeepkist assembly.

Only public members may appear: the game libs we compile against are publicized, so private members
compile but fail at runtime. Those must go through Access.Get/Set. Needs: pip install dnfile
Usage: python tools/memberrefs.py bin/Release/CouchParty.dll
"""
import sys, dnfile
pe = dnfile.dnPE(sys.argv[1]); md = pe.net.mdtables
asm = {i+1: str(r.Name) for i, r in enumerate(md.AssemblyRef.rows)}
def scope_of(tr):
    rs = tr.ResolutionScope
    try:
        row = rs.row
        if hasattr(row, 'TypeName'): return scope_of(row)  # nested type
        return str(row.Name)
    except Exception: return '?'
for m in md.MemberRef.rows:
    cls = m.Class
    row = cls.row
    if not hasattr(row, 'TypeName'):
        continue
    sc = scope_of(row)
    if sc != 'Zeepkist': continue
    sig = m.Signature if isinstance(m.Signature, (bytes, bytearray)) else bytes(m.Signature.value if hasattr(m.Signature,'value') else m.Signature)
    kind = 'FIELD ' if sig and sig[0] == 0x06 else 'method'
    print(kind, f"{row.TypeName}.{m.Name}")
