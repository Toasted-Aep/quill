"""Insert this run's entry directly after the file's title line, keeping the
document wholly CRLF.  Byte-level, and asserted both ways: the endings are
measured before and after and the old body must survive untouched.

Not a shell heredoc - the backslash-eating hazard is documented and this file
is the only surviving record of three runs' work."""
import os

root = r'C:\Users\irony\Downloads\Quill Gem - Fable\Quill'
doc = os.path.join(root, 'docs', 'VISUAL-PASS-RESUME.md')
entry = os.path.join(root, 'scratchpad', 'vp9_entry.md')

orig = open(doc, 'rb').read()
before = (orig.count(b'\r\n'), orig.count(b'\n') - orig.count(b'\r\n'))
assert before[1] == 0, 'doc had bare LF before the write: %r' % (before,)
assert not orig.startswith(b'\xef\xbb\xbf')

TITLE = b'# Visual verification pass \xe2\x80\x94 resume state\r\n\r\n'
assert orig.startswith(TITLE), 'title line is not what was measured'

body = open(entry, 'rb').read()
body = body.replace(b'\r\n', b'\n').replace(b'\n', b'\r\n')   # normalise to CRLF
if not body.endswith(b'\r\n'):
    body += b'\r\n'

out = TITLE + body + orig[len(TITLE):]
open(doc, 'wb').write(out)

new = open(doc, 'rb').read()
after = (new.count(b'\r\n'), new.count(b'\n') - new.count(b'\r\n'))
assert after[1] == 0, 'bare LF introduced: %r' % (after,)
assert new.endswith(orig[len(TITLE):]), 'the old body did not survive verbatim'
assert new[:3] != b'\xef\xbb\xbf'
print('doc %d -> %d bytes' % (len(orig), len(new)))
print('CRLF %d -> %d   bare LF %d -> %d' % (before[0], after[0], before[1], after[1]))
print('entry inserted: %d bytes, old body verbatim' % len(body))
