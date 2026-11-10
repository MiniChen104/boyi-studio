import {build} from 'esbuild';
import {copyFileSync, mkdirSync} from 'node:fs';
await build({entryPoints:['src/main.jsx'],bundle:true,minify:true,format:'iife',target:'chrome120',outfile:'../glass-oj/ui/liquid-ui.js',define:{'process.env.NODE_ENV':'"production"'}});
mkdirSync('../glass-oj/ui/vendor/licenses',{recursive:true});
for(const name of ['liquid-glass-react','react','react-dom','scheduler'])copyFileSync(`node_modules/${name}/LICENSE`,`../glass-oj/ui/vendor/licenses/${name}.txt`);
