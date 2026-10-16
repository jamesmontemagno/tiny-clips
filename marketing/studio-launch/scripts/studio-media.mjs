import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { projectDir } from './static-server.mjs';

// Decoded stills keep footage frame-accurate when the renderer seeks out of order.
export async function prepareStudioFrames() {
  const source = join(projectDir, 'assets', 'studio-demo.mp4');
  if (!existsSync(source)) throw new Error(`Studio footage is missing: ${source}`);
  const frames = join(projectDir, 'out', 'studio-frames');
  rmSync(frames, { recursive: true, force: true });
  mkdirSync(frames, { recursive: true });
  await new Promise((resolve, reject) => {
    const ffmpeg = spawn('ffmpeg', [
      '-hide_banner', '-loglevel', 'error', '-y', '-i', source,
      '-vf', 'fps=30', '-frames:v', '225', '-q:v', '2', '-start_number', '0',
      join(frames, 'frame-%03d.jpg'),
    ], { stdio: 'inherit' });
    ffmpeg.on('error', reject);
    ffmpeg.on('exit', (code) => code === 0 ? resolve() : reject(new Error(`Studio frame extraction exited with ${code}`)));
  });
  if (!existsSync(join(frames, 'frame-224.jpg'))) throw new Error('Studio footage must provide 225 frames at 30 fps.');
}
