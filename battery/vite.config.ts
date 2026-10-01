import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

import path from 'path';

export default defineConfig({
  plugins: [react()],
  build: {
    target: 'es2020',
    outDir: 'dist',
    assetsDir: 'assets',
    sourcemap: false,
    minify: true,
    base: './', // battery/dist에서 실행되는 번들에 맞게 상대 경로 베이스 사용
    // 이 설정 파일은 battery/ 아래에 있으므로, entry를 battery/index.html로 지정한다.
    rollupOptions: {
      input: path.resolve(__dirname, 'index.html'),
    },
  },
  server: {
    port: 5174,
  },
});
