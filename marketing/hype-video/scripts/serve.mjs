// Live preview with a scrubber and the soundtrack: npm run preview
import { compositionPath, startServer } from './static-server.mjs';

const { port } = await startServer(Number(process.env.PORT ?? 4173));
console.log(`Tiny Clips hype video preview: http://127.0.0.1:${port}${compositionPath}`);
console.log('Space plays/pauses, arrow keys step a beat. The selected soundtrack is generated before preview starts.');
