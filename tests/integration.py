"""End-to-end checks using only Python's standard library and a .NET SDK."""
import hashlib
import http.client
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
from urllib.parse import quote, urlsplit

ROOT = Path(__file__).resolve().parents[1]
DOTNET = os.environ.get('DOTNET', 'dotnet')
subprocess.run([DOTNET, 'build', '-c', 'Release', '--nologo'], cwd=ROOT, check=True)
DLL = ROOT / 'bin/Release/net10.0/FileHarbor.dll'

with tempfile.TemporaryDirectory(prefix='fileharbor-test-') as tmp:
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    origin = f'http://127.0.0.1:{port}'
    env = dict(os.environ, ASPNETCORE_URLS=origin, PublicUrl=origin,
               Storage__Path=tmp, Storage__MaxFileBytes='65536', Storage__QuotaBytes='10485760',
               Storage__UploadKey='test-secret')
    process = None
    log = tempfile.TemporaryFile()

    def request(method, path, data=None, headers=None):
        connection = http.client.HTTPConnection('127.0.0.1', port, timeout=20)
        connection.request(method, path, data, headers or {})
        response = connection.getresponse()
        result = response.status, dict(response.getheaders()), response.read()
        connection.close()
        return result

    def start():
        global process
        process = subprocess.Popen([DOTNET, str(DLL)], cwd=ROOT, env=env, stdout=log, stderr=log)
        for _ in range(100):
            if process.poll() is not None:
                log.seek(0)
                raise AssertionError(log.read().decode())
            try:
                if request('GET', '/healthz')[0] == 200:
                    return
            except OSError:
                pass
            time.sleep(.05)
        raise AssertionError('Server failed to start')

    def stop():
        process.terminate()
        process.wait(timeout=10)

    def upload(name, content, **extra):
        return request('POST', '/api/files?name=' + quote(name, safe=''), content,
                       {'Content-Type': 'application/octet-stream', 'X-Upload-Key': 'test-secret', **extra})

    try:
        start()
        code, _, body = request('GET', '/')
        assert code == 200 and b'vendor/7.css' in body
        assert request('GET', '/vendor/7.css')[0] == 200
        assert request('GET', '/embed.png')[0] == 200
        assert request('POST', '/api/files?name=a', b'hello')[0] == 401
        assert upload('a', b'hello', Origin='https://evil.example')[0] == 403
        assert upload('empty', b'')[0] == 400
        assert upload('big', b'x' * 65537)[0] == 413
        assert request('GET', '/f/invalid')[0] == 404
        assert request('GET', '/preview/invalid')[0] == 404
        saved = []
        for name, content, compressed in [
            ('notes.txt', b'FileHarbor lossless test\n' * 2000, True),
            ('random.bin', os.urandom(16000), False),
            ('../<script>alert(1)</script>.html', b'<script>alert(1)</script>', False),
            ('hello ☃.txt', b'hello snowman', False),
            ('..', b'dot filename', False),
        ]:
            code, _, response = upload(name, content, Origin=origin)
            assert code == 201, (code, response)
            result = json.loads(response)
            assert result['compressed'] == compressed
            assert result['sha256'] == hashlib.sha256(content).hexdigest()
            path = urlsplit(result['downloadUrl']).path
            code, headers, downloaded = request('GET', path)
            assert code == 200 and downloaded == content
            assert 'attachment' in headers['Content-Disposition']
            assert int(headers['Content-Length']) == len(content)
            assert headers['X-Content-Type-Options'] == 'nosniff'
            assert request('HEAD', path)[2] == b''
            code, _, page = request('GET', urlsplit(result['shareUrl']).path)
            assert code == 200 and b'og:title' in page and b'og:image' in page
            assert b'<script>alert(1)</script>' not in page
            saved.append((path, content))
        png = (ROOT / 'wwwroot/embed.png').read_bytes()
        code, _, response = upload('preview.png', png)
        assert code == 201, response
        image = json.loads(response)
        image_id = image['shareUrl'].rsplit('/', 1)[1]
        code, headers, content = request('GET', '/preview/' + image_id)
        assert code == 200 and content == png and headers['Content-Type'] == 'image/png'
        assert ('/preview/' + image_id).encode() in request('GET', '/f/' + image_id)[2]
        # Interrupted writes are cleaned, committed files survive a process restart.
        stop()
        Path(tmp, 'unfinished.raw.tmp').write_bytes(b'partial')
        orphan = Path(tmp, 'f' * 32 + '.blob')
        orphan.write_bytes(b'orphan')
        start()
        assert not Path(tmp, 'unfinished.raw.tmp').exists() and not orphan.exists()
        for path, expected in saved:
            assert request('GET', path)[2] == expected
        # A low quota rejects uploads safely, without committing metadata.
        stop()
        env['Storage__QuotaBytes'] = '70000'
        start()
        assert upload('no-space', b'hello')[0] == 507
        # Anonymous mode works when no key is configured.
        stop()
        env['Storage__QuotaBytes'] = '10485760'
        env['Storage__UploadKey'] = ''
        start()
        assert request('POST', '/api/files?name=anonymous.txt', b'hello')[0] == 201
        print('PASS: upload/auth/origin/limits/quota, lossless round trips, safe filenames, image previews, embeds, restart recovery, anonymous mode')
    finally:
        if process is not None and process.poll() is None:
            stop()
        log.close()
