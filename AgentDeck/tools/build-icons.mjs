// Builds wwwroot/icons/fluent.json from Microsoft's Fluent Emoji (flat, MIT) via @iconify-json/fluent-emoji-flat.
// Usage: npm install @iconify-json/fluent-emoji-flat && node tools/build-icons.mjs <path-to-node_modules>
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const modules = process.argv[2] ?? 'node_modules';
const src = join(modules, '@iconify-json', 'fluent-emoji-flat');
const set = JSON.parse(readFileSync(join(src, 'icons.json'), 'utf8'));
const meta = JSON.parse(readFileSync(join(src, 'metadata.json'), 'utf8'));

// Skin-tone variants multiply People & Body ~5x; keep only the default yellow ones.
const skinTone = /-(light|medium-light|medium|medium-dark|dark)(-skin-tone)?$|-skin-tone/;
const keep = name => set.icons[name] && !skinTone.test(name);

// Hand-picked icons that suit projects and coding workflows; shown first in the picker.
const featured = [
  'rocket', 'high-voltage', 'fire', 'sparkles', 'gem-stone', 'crystal-ball', 'brain', 'robot', 'alien-monster',
  'flying-saucer', 'satellite', 'milky-way', 'rainbow', 'sun', 'crescent-moon', 'star', 'glowing-star', 'comet',
  'bug', 'lady-beetle', 'test-tube', 'microscope', 'dna', 'magnet', 'gear', 'hammer-and-wrench', 'toolbox',
  'wrench', 'nut-and-bolt', 'hammer', 'axe', 'pick', 'laptop', 'desktop-computer', 'keyboard', 'floppy-disk',
  'joystick', 'video-game', 'puzzle-piece', 'package', 'card-file-box', 'file-cabinet', 'open-file-folder',
  'books', 'memo', 'page-facing-up', 'clipboard', 'pushpin', 'bookmark-tabs', 'magnifying-glass-tilted-left',
  'bar-chart', 'chart-increasing', 'money-bag', 'globe-showing-americas', 'world-map', 'compass', 'bullseye',
  'light-bulb', 'artist-palette', 'paintbrush', 'clapper-board', 'musical-note', 'camera', 'locked', 'key',
  'shield', 'bell', 'megaphone', 'speech-balloon', 'envelope', 'calendar', 'alarm-clock', 'hourglass-not-done',
  'broom', 'recycling-symbol', 'check-mark-button', 'construction', 'building-construction', 'house',
  'classical-building', 'cityscape', 'shopping-cart', 'credit-card', 'mobile-phone', 'battery', 'electric-plug',
  'link', 'chains', 'anchor', 'sailboat', 'airplane', 'racing-car', 'bicycle', 'mountain', 'volcano',
  'evergreen-tree', 'four-leaf-clover', 'seedling', 'cactus', 'mushroom', 'owl', 'fox', 'octopus', 'dolphin',
  'whale', 'butterfly', 'honeybee', 'turtle', 'dragon', 'unicorn', 'penguin', 'crab', 'lion', 'panda',
  'hot-beverage', 'pizza', 'doughnut', 'birthday-cake', 'trophy', 'sports-medal', 'party-popper', 'balloon',
  'wrapped-gift', 'crown', 'ghost', 'skull', 'smiling-face-with-sunglasses', 'nerd-face', 'thinking-face',
  'red-heart', 'orange-heart', 'yellow-heart', 'green-heart', 'blue-heart', 'purple-heart',
].filter(keep);

const categories = { Featured: featured };
for (const [category, names] of Object.entries(meta.categories))
  categories[category] = names.filter(keep);

const used = new Set(Object.values(categories).flat());
const icons = Object.fromEntries([...used].sort().map(name => [name, set.icons[name].body]));

// Search keywords from emojilib (MIT): icon name -> emoji character (via chars.json) -> keywords.
const emojilib = JSON.parse(readFileSync(join(modules, 'emojilib', 'dist', 'emoji-en-US.json'), 'utf8'));
const chars = JSON.parse(readFileSync(join(src, 'chars.json'), 'utf8'));
const keywords = {};
for (const [hex, name] of Object.entries(chars)) {
  if (!used.has(name)) continue;
  const emoji = String.fromCodePoint(...hex.split('-').map(h => parseInt(h, 16)));
  const words = emojilib[emoji] ?? emojilib[emoji.replace(/️/g, '')] ?? emojilib[emoji + '️'];
  if (!words) continue;
  const extra = [...new Set(words.flatMap(w => w.toLowerCase().split(/[_\s]+/)))]
    .filter(w => w.length > 1 && !name.split('-').includes(w));
  if (extra.length) keywords[name] = extra.join(' ');
}

const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'wwwroot', 'icons', 'fluent.json');
mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, JSON.stringify({ size: set.width ?? 32, categories, icons, keywords }));
console.log(`${Object.keys(icons).length} icons, ${featured.length} featured, ${Object.keys(keywords).length} with keywords -> ${out}`);
