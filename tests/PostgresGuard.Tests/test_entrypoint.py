#!/usr/bin/env python3
"""Offline process tests: the shell is real; only dotnet/sleep are controlled peers."""
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'src/MudXtra.ThemeCreator.UI/docker-entrypoint.sh'

class GuardTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.path = Path(self.tmp.name)
        self.events = self.path / 'events'
        self.write('dotnet', '''#!/bin/sh
if [ "$2" = "--postgres-probe" ]; then
  n=0; [ ! -f "$STATE/count" ] || n=$(cat "$STATE/count")
  n=$((n+1)); echo "$n" > "$STATE/count"; echo "probe:$n" >> "$STATE/events"
  if [ "$MODE" = block ]; then
    trap 'echo stopped >> "$STATE/events"; exit 130' INT TERM
    while :; do /bin/sleep 0.05; done
  fi
  [ "$MODE" != permanent ] || exit 20
  [ "$MODE" != exhausted ] || exit 10
  [ "$MODE" != retry ] || [ "$n" -ge 3 ] || exit 10
  exit 0
fi
echo "app:$$" >> "$STATE/events"
[ "$MODE" != race ] || exit 42
exit 0
''')
        self.write('sleep', '''#!/bin/sh
echo "sleep:$1" >> "$STATE/events"
if [ "$MODE" = wait ]; then
  trap 'echo wait-stopped >> "$STATE/events"; exit 130' TERM INT
  while :; do /bin/sleep 0.05; done
fi
exit 0
''')

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, name, content):
        file = self.path / name
        file.write_text(content)
        file.chmod(0o755)

    def start(self, mode):
        self.assertTrue(SCRIPT.is_file(), 'Docker entry guard missing')
        return subprocess.Popen(['/bin/sh', str(SCRIPT)], env={**os.environ, 'PATH': str(self.path)+':'+os.environ['PATH'], 'STATE': str(self.path), 'MODE': mode}, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

    def run_mode(self, mode):
        process = self.start(mode)
        out, err = process.communicate(timeout=4)
        return process.returncode, self.events.read_text().splitlines(), out+err

    def test_same_arguments_reach_probe_and_app(self):
        self.write('dotnet', '''#!/bin/sh
printf '%s\\n' "$*" >> "$STATE/events"
exit 0
''')
        process = subprocess.Popen(['/bin/sh', str(SCRIPT), '--ConnectionStrings:postgresql=Host=fixture;Database=d;Username=u'], env={**os.environ, 'PATH': str(self.path)+':'+os.environ['PATH'], 'STATE': str(self.path)}, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        process.communicate(timeout=4)
        events = self.events.read_text().splitlines()
        self.assertEqual(len(events), 2)
        self.assertIn('--ConnectionStrings:postgresql=Host=fixture;Database=d;Username=u', events[0])
        self.assertIn('--ConnectionStrings:postgresql=Host=fixture;Database=d;Username=u', events[1])

    def test_immediate_success_exec(self):
        process = self.start('success')
        process.communicate(timeout=4)
        self.assertEqual(process.returncode, 0)
        self.assertEqual(self.events.read_text().splitlines(), ['probe:1', f'app:{process.pid}'])

    def test_transient_waits_begin_after_failure(self):
        code, events, _ = self.run_mode('retry')
        self.assertEqual(code, 0)
        self.assertEqual(events[:-1], ['probe:1', 'sleep:60', 'probe:2', 'sleep:60', 'probe:3'])

    def test_exhausted_has_six_attempts_five_waits_no_app(self):
        code, events, _ = self.run_mode('exhausted')
        self.assertEqual(code, 10)
        self.assertEqual(events, sum(([f'probe:{i}', 'sleep:60'] for i in range(1, 6)), []) + ['probe:6'])

    def test_permanent_is_immediate(self):
        code, events, _ = self.run_mode('permanent')
        self.assertEqual(code, 20)
        self.assertEqual(events, ['probe:1'])

    def test_app_failure_after_success_is_not_retried(self):
        code, events, _ = self.run_mode('race')
        self.assertEqual(code, 42)
        self.assertEqual(events[0], 'probe:1')
        self.assertEqual(len(events), 2)

    def test_shutdown_during_probe_reaps_child(self):
        process = self.start('block')
        deadline = time.monotonic()+2
        while (not self.events.exists()) and time.monotonic()<deadline:
            time.sleep(.01)
        process.send_signal(signal.SIGTERM)
        process.communicate(timeout=2)
        self.assertEqual(process.returncode, 143)
        self.assertIn('stopped', self.events.read_text())
        self.assertNotIn('app:', self.events.read_text())

    def test_shutdown_during_wait_reaps_child(self):
        # First probe transient; make sleep block, not the probe.
        dotnet = (self.path / 'dotnet').read_text().replace('[ "$MODE" != exhausted ]', '[ "$MODE" != wait ]')
        self.write('dotnet', dotnet)
        process = self.start('wait')
        deadline = time.monotonic()+2
        while (not self.events.exists() or 'sleep:60' not in self.events.read_text()) and time.monotonic()<deadline:
            time.sleep(.01)
        process.send_signal(signal.SIGTERM)
        process.communicate(timeout=2)
        self.assertEqual(process.returncode, 143)
        self.assertIn('wait-stopped', self.events.read_text())
        self.assertNotIn('app:', self.events.read_text())

    def test_no_provider_fallback_and_probe_before_build(self):
        extension = (ROOT/'src/MudXtra.ThemeCreator.UI/Extensions/IServiceCollectionExtensions.cs').read_text()
        self.assertNotIn('UseSqlServer', extension, 'legacy fallback must be removed')
        self.assertNotIn('OpenAsync', extension, 'registration cannot race by probing again')
        program = (ROOT/'src/MudXtra.ThemeCreator.UI/Program.cs').read_text()
        self.assertIn('--postgres-probe', program)
        self.assertLess(program.index('PostgresReadiness.ProbeAsync'), program.index('builder.Build()'))
        docker = (ROOT/'src/MudXtra.ThemeCreator.UI/Dockerfile').read_text()
        self.assertIn('ENTRYPOINT ["/bin/sh", "/app/docker-entrypoint.sh"]', docker)

if __name__ == '__main__':
    unittest.main(verbosity=2)
