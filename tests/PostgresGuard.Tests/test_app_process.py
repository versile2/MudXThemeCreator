#!/usr/bin/env python3
"""Real net9 application processes against an isolated PostgreSQL wire peer (no DB/DDL)."""
import os
from pathlib import Path
import signal
import socket
import struct
import subprocess
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
UI = ROOT / 'src/MudXtra.ThemeCreator.UI'
DLL = UI / 'bin/Debug/net9.0/MudXtra.ThemeCreator.UI.dll'
DOTNET = Path(os.environ.get('DOTNET_ROOT', '/home/versile/.dotnet')) / 'dotnet'

class Peer:
    def __init__(self, mode):
        self.mode = mode
        self.socket = socket.socket()
        self.socket.bind(('127.0.0.1', 0))
        self.socket.listen()
        self.socket.settimeout(.1)
        self.port = self.socket.getsockname()[1]
        self.clients = []
        self.stop = threading.Event()
        self.closed = threading.Event()
        self.auth = threading.Event()
        self.query_started = threading.Event()
        self.queries = []
        self.connections = 0
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()

    def read(self, client, count):
        data = b''
        while len(data) < count:
            part = client.recv(count-len(data))
            if not part:
                self.closed.set()
                raise EOFError()
            data += part
        return data

    def send(self, client, kind, data):
        client.sendall(kind.encode() + struct.pack('!i', len(data)+4) + data)

    def run(self):
        while not self.stop.is_set():
            try:
                client, _ = self.socket.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            self.clients.append(client)
            self.connections += 1
            try:
                size = struct.unpack('!i', self.read(client, 4))[0]
                self.read(client, size-4)
                self.auth.set()
                mode = 'auth-error' if self.mode == 'race' and self.connections > 1 else self.mode
                if mode == 'stall':
                    while client.recv(4096):
                        pass
                    self.closed.set()
                    continue
                if mode == 'auth-error':
                    self.send(client, 'E', b'SFATAL\0C28P01\0Mprivate-secret\0\0')
                    continue
                self.send(client, 'R', struct.pack('!i', 0))
                self.send(client, 'S', b'server_version\0' + b'15.0\0')
                self.send(client, 'S', b'client_encoding\0UTF8\0')
                self.send(client, 'K', struct.pack('!ii', 123, 456))
                self.send(client, 'Z', b'I')
                while True:
                    kind = self.read(client, 1)
                    size = struct.unpack('!i', self.read(client, 4))[0]
                    body = self.read(client, size-4)
                    if kind == b'X':
                        self.closed.set()
                        break
                    if kind == b'P':
                        self.queries.append(body.split(b'\0')[1])
                        self.query_started.set()
                        if mode == 'stall-query':
                            while client.recv(4096):
                                pass
                            self.closed.set()
                            break
                        self.send(client, '1', b'')
                    if kind == b'B':
                        self.send(client, '2', b'')
                    if kind == b'D':
                        self.send(client, 'T', struct.pack('!h', 1) + b'?column?\0' + struct.pack('!ihihih', 0, 0, 23, 4, -1, 1))
                    if kind == b'E':
                        self.send(client, 'D', struct.pack('!hi', 1, 4) + struct.pack('!i', 1))
                        self.send(client, 'C', b'SELECT 1\0')
                    if kind == b'S':
                        self.send(client, 'Z', b'I')
            except (OSError, EOFError):
                pass
            finally:
                client.close()

    def close(self):
        self.stop.set()
        self.socket.close()
        for client in self.clients:
            try:
                client.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            client.close()
        self.thread.join(timeout=2)

class AppProcessTests(unittest.TestCase):
    def setUp(self):
        self.peer = None

    def tearDown(self):
        if self.peer:
            self.peer.close()

    def start(self, mode, command=None, probe=True):
        self.assertTrue(DLL.is_file(), 'build actual net9 app first')
        self.peer = Peer(mode)
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            self.http_port = reservation.getsockname()[1]
        env = {**os.environ, 'DOTNET_ROOT': str(DOTNET.parent), 'PATH': str(DOTNET.parent)+':'+os.environ['PATH'], 'ConnectionStrings__postgresql': f'Host=127.0.0.1;Port={self.peer.port};Username=u;Password=p;Database=d;SSL Mode=Disable', 'ASPNETCORE_URLS': f'http://127.0.0.1:{self.http_port}', 'ASPNETCORE_ENVIRONMENT': 'Production'}
        argv = command or [str(DOTNET), str(DLL)] + (['--postgres-probe'] if probe else [])
        return subprocess.Popen(argv, cwd=DLL.parent, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

    def assert_not_serving(self):
        with self.assertRaises(OSError, msg='HTTP socket must not accept before readiness'):
            socket.create_connection(('127.0.0.1', self.http_port), timeout=.2)

    def test_actual_mode_success_only_select_and_no_host(self):
        process = self.start('success')
        out, err = process.communicate(timeout=4)
        self.assertEqual(process.returncode, 0, out+err)
        self.assertEqual(self.peer.queries, [b'SELECT 1'])
        self.assertTrue(self.peer.closed.wait(1))
        self.assertNotIn('Now listening', out+err)

    def test_actual_auth_failure_redacted_no_host(self):
        process = self.start('auth-error', probe=False)
        out, err = process.communicate(timeout=4)
        self.assertEqual(process.returncode, 20, out+err)
        self.assertNotIn('private-secret', out+err)
        self.assertNotIn('Now listening', out+err)
        self.assertEqual(self.peer.queries, [])

    def test_real_hard_deadline_no_host_and_socket_closed(self):
        start = time.monotonic()
        process = self.start('stall', probe=False)
        out, err = process.communicate(timeout=12)
        self.assertEqual(process.returncode, 10, out+err)
        self.assertLess(time.monotonic()-start, 11.5)
        self.assertTrue(self.peer.closed.wait(1))
        self.assertNotIn('Now listening', out+err)

    def test_signal_during_real_startup_prompt_no_host_and_socket_closed(self):
        process = self.start('stall', probe=False)
        self.assertTrue(self.peer.auth.wait(2))
        self.assert_not_serving()
        start = time.monotonic()
        process.send_signal(signal.SIGTERM)
        out, err = process.communicate(timeout=2)
        self.assertEqual(process.returncode, 130, out+err)
        self.assertLess(time.monotonic()-start, 1.5)
        self.assertTrue(self.peer.closed.wait(1))
        self.assertNotIn('Now listening', out+err)

    def test_query_deadline_includes_driver_cancel_handshake(self):
        start = time.monotonic()
        process = self.start('stall-query', probe=False)
        self.assertTrue(self.peer.query_started.wait(2))
        self.assert_not_serving()
        out, err = process.communicate(timeout=12)
        self.assertEqual(process.returncode, 10, out+err)
        self.assertLess(time.monotonic()-start, 11.5)
        self.assertTrue(self.peer.closed.wait(1))
        self.assert_not_serving()

    def test_signal_during_real_query_closes_socket_promptly(self):
        process = self.start('stall-query', probe=False)
        self.assertTrue(self.peer.query_started.wait(2))
        self.assert_not_serving()
        start = time.monotonic()
        process.send_signal(signal.SIGTERM)
        out, err = process.communicate(timeout=2)
        self.assertEqual(process.returncode, 130, out+err)
        self.assertLess(time.monotonic()-start, 1.5)
        self.assertTrue(self.peer.closed.wait(1))

    def test_real_shell_guard_success_then_app_failure_no_provider_fallback(self):
        process = self.start('race', command=['/bin/sh', str(UI/'docker-entrypoint.sh')])
        out, err = process.communicate(timeout=5)
        self.assertEqual(process.returncode, 20, out+err)
        self.assertEqual(self.peer.connections, 2)
        self.assertEqual(self.peer.queries, [b'SELECT 1'])
        self.assertNotIn('Now listening', out+err)
        self.assertNotIn('LocalDB', out+err)
        self.assertNotIn('waiting 60', out+err)

if __name__ == '__main__':
    unittest.main(verbosity=2)
