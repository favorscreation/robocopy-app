# Backup Desk

Windows向けの、手動で実行する差分バックアップ・復元アプリです。複数フォルダーと並列バックアップ、差分確認後の上書き復元に対応します。

## 使い始める

1. 実行用の `BackupDesk-win-x64.zip` を展開します（[ソースから作成する場合](docs/development.md)）。
2. `BackupDesk.exe` を起動し、コピー元フォルダーと保存先を指定します。
3. 「差分を確認」で対象を確認し、「バックアップを実行」を押します。

Windows x64向けで、.NETの追加インストールは不要です。上書き方式のため過去の世代は保持しません。スケジュール実行はありません。

## ドキュメント

- [使い方](docs/usage.md) — バックアップ・復元・複数フォルダー・並列実行・設定
- [動作と制限](docs/behavior.md) — 差分の判定、対応環境、保存される情報
- [開発・ビルド](docs/development.md) — 技術構成、ビルド、テスト、配布
- [検証記録](docs/validation.md) — 実施済みの確認と未検証の範囲

技術構成は C# / .NET 10 LTS / WPF。同梱.NETのライセンス・通知は [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) を参照してください。
