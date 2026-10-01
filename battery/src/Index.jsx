import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BatteryRenderer } from './BatteryRenderer';

// WebView2에서 배터리 상태를 전달받아 렌더링
// WPF(MainWindow.xaml.cs)에서 WebView2.InvokeScript 또는 URL 쿼리로 %percent%, %charging%, %paused%를 주입
const params = new URLSearchParams(window.location.search);
const percent = parseFloat(params.get('percent') ?? '1');
const charging = params.get('charging') === 'true';
const paused = params.get('paused') === 'true';

createRoot(document.getElementById('root')).render(
  <StrictMode>
    <BatteryRenderer percent={percent} charging={charging} paused={paused} />
  </StrictMode>
);
