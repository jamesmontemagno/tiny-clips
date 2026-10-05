// Shared static file server for preview and rendering. Serves the repository root so the
// composition can reference the real app artwork in /docs without copying it.
import { createServer } from 'node:http';
import { createReadStream, statSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

export const projectDir = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const repoRoot = resolve(projectDir, '../..');
export const compositionPath = '/marketing/hype-video/src/index.html';

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.svg': 'image/svg+xml',
  '.wav': 'audio/wav',
  '.mp4': 'video/mp4',
};

export function startServer(port = 0) {
  const server = createServer((req, res) => {
    const pathname = decodeURIComponent(new URL(req.url, 'http://localhost').pathname);
    const file = normalize(join(repoRoot, pathname));
    if (file !== repoRoot && !file.startsWith(repoRoot + sep)) {
      res.writeHead(403).end();
      return;
    }
    let stat;
    try { stat = statSync(file); } catch { stat = null; }
    if (!stat || !stat.isFile()) {
      res.writeHead(404).end('Not found');
      return;
    }
    res.writeHead(200, {
      'Content-Type': TYPES[extname(file)] ?? 'application/octet-stream',
      'Content-Length': stat.size,
      'Cache-Control': 'no-store',
    });
    createReadStream(file).pipe(res);
  });
  return new Promise((done) => {
    server.listen(port, '127.0.0.1', () => done({ server, port: server.address().port }));
  });
}
