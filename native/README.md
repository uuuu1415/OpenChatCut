# OpenChatCut Native Desktop

This directory is the Windows-native frontend migration target.

- The UI is WPF with native Windows controls and GPU-composited panels.
- The process never creates a WebView, BrowserWindow, or Electron runtime.
- `desktop/native-service.ts` is a headless Node service. It owns the existing
  project store, EditorCore reducer, MCP, AI, media and export services.
- The only UI/service boundary is the authenticated JSON contract in
  `shared/native-desktop-contract.ts`.

The migration is intentionally incremental: domain operations remain in the
existing TypeScript service until each native screen has parity coverage. The
old renderer is not used by the native client and will be removed after the
parity gate is complete.
