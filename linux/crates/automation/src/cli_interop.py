"""Wire client for the ignored Rust interop test; never loaded by the product."""
import asyncio
import json
import os
import stat
import sys

CLI = sys.argv[1]
TIMEOUT = int(os.environ['TINYCLIPS_TEST_IO_TIMEOUT_SECONDS'])


async def cli(*args):
    child = await asyncio.create_subprocess_exec(CLI, *args, stdout=asyncio.subprocess.PIPE,
                                                  stderr=asyncio.subprocess.PIPE)
    try:
        stdout, stderr = await asyncio.wait_for(child.communicate(), TIMEOUT)
    finally:
        if child.returncode is None:
            child.kill()
            await child.wait()
    if child.returncode != 0:
        # Do not echo client credentials, request payloads, or complete CLI output.
        raise AssertionError(f"{args[0]}: exit {child.returncode}")
    return json.loads(stdout)


async def main():
    paired = await cli('pair', '--name', 'transport fixture', '--scopes', 'read,settings')
    assert paired['paired'] and paired['scopes'] == ['read']
    assert stat.S_IMODE(os.stat(os.environ['TINYCLIPS_AGENT_CONFIG']).st_mode) == 0o600
    print('CLI: native Linux discovery, pairing poll, read-only grant, private mode 0600 config', flush=True)
    server = await asyncio.create_subprocess_exec(CLI, 'mcp', stdin=asyncio.subprocess.PIPE,
                                                   stdout=asyncio.subprocess.PIPE)
    async def write(message):
        server.stdin.write((json.dumps(dict(jsonrpc='2.0', **message)) + '\n').encode())
        await server.stdin.drain()

    async def request(id, method, params):
        await write(dict(id=id, method=method, params=params))
        while True:
            line = await asyncio.wait_for(server.stdout.readline(), TIMEOUT)
            assert line, 'MCP closed before replying'
            packet = json.loads(line)
            if packet.get('id') == id:
                return packet['result']

    async def call(id, name, args, expect_error):
        r = await request(id, 'tools/call', {'name': name, 'arguments': args})
        body = json.loads(r['content'][0]['text'])
        assert r.get('isError', False) == expect_error, (name, body.get('error', {}).get('code'))
        return body

    try:
        init = await request(1, 'initialize', {'protocolVersion': '2025-06-18', 'capabilities': {},
                             'clientInfo': {'name': 'transport-test', 'version': '1'}})
        assert init['serverInfo']['name'] == 'tinyclips'
        await write(dict(method='notifications/initialized'))
        print('MCP: negotiated stdio with the published .NET server', flush=True)
        hello = await call(2, 'tinyclips_status', {}, False)
        assert hello['platform'] == 'linux' and hello['automationEnabled']
        assert set(hello['commands']) == {'app.status', 'settings.set', 'session.hello', 'session.pair', 'session.pairStatus'}
        assert (await call(3, 'tinyclips_app_status', {}, False))['app'] == 'transport fixture'
        print('MCP: discovered host capabilities; authenticated tool returned fixture state', flush=True)
        assert (await call(4, 'tinyclips_settings_set', {'key':'recordAudio','value':True}, True))['error']['code'] == 'missing_scope'
        assert (await call(5, 'tinyclips_capture_ocr', {}, True))['error']['code'] == 'not_supported'
        os.unlink(os.environ['TINYCLIPS_AGENT_CONFIG'])  # forget only the isolated fixture token
        assert (await call(6, 'tinyclips_app_status', {}, True))['error']['code'] == 'unauthorized'
        print('MCP: missing_scope, not_supported and unauthorized all enforced by live host', flush=True)
        server.stdin.close()
        assert await asyncio.wait_for(server.wait(), 30) == 0
    finally:
        if server.returncode is None:
            server.kill()
            await server.wait()


asyncio.run(main())
