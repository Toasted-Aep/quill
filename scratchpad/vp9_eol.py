import sys, os
p = sys.argv[1]
b = open(p, 'rb').read()
crlf = b.count(b'\r\n')
lf = b.count(b'\n')
cr = b.count(b'\r')
print(p)
print('  bytes      :', len(b))
print('  CRLF       :', crlf)
print('  bare LF    :', lf - crlf)
print('  bare CR    :', cr - crlf)
print('  NUL        :', b.count(b'\x00'))
print('  BOM        :', b[:3] == b'\xef\xbb\xbf')
