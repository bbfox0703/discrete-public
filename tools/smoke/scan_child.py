"""Child 'UI' that starts a long scan and then gets force-killed by the parent
(E-4/E-5: the DLL must notice the dead client and abort, without spinning).

argv: <value> <flagfile> [mode]   mode = beginValueScan (default) | scanFieldValue
"""
import json, sys, time
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
sys.path.insert(0, _HERE)
import dh

value = int(sys.argv[1])
flag = sys.argv[2]
mode = sys.argv[3] if len(sys.argv) > 3 else 'beginValueScan'
r = dh.Rpc(timeout=15)
r.call('ping')
with open(flag, 'w') as f:
    f.write('STARTED %s %f\n' % (mode, time.time()))
try:
    if mode == 'scanFieldValue':
        res = r.call('scanFieldValue', {'dataType': 'int32', 'value': value, 'limit': 200})
    else:
        res = r.call('beginValueScan', {'dataType': 'int32', 'scanType': 'exact',
                                        'value': value, 'limit': 200, 'retainCap': 200000})
    with open(flag, 'a') as f:
        f.write('FINISHED count=%s\n' % res.get('count'))
except Exception as e:
    with open(flag, 'a') as f:
        f.write('ERROR %s\n' % e)
