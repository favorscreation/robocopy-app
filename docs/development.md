# 開発・ビルド

[READMEへ戻る](../README.md) · [検証記録](validation.md)

## 技術構成

C# / .NET 10 LTS / WPFで構成し、外部UIライブラリは使用していません。コピー処理は.NETのファイルI/O、設定の保存はJSONです。

| 場所 | 内容 |
| --- | --- |
| `src/ManualBackup.App` | WPF画面、設定、ログ。ターゲットは `net10.0-windows` |
| `src/ManualBackup.Core` | 差分確認、バックアップ、複数組の復元、複数コピー元の同時実行。ターゲットは `net10.0` |
| `tests` | コンソール形式の統合・設定・WPF連携・負荷テスト |
| `build.ps1` | テスト、自己完結EXEの発行、配布ZIPの作成 |

## 必要な環境

Windowsと.NET SDK 10.0.404以降の10.0系を用意します。`global.json` の `rollForward: latestFeature` により、同じ10.0系の新しいSDKを利用できます。初回のビルドには、必要な.NETパックを取得できるネットワーク接続が必要です。

以下のコマンドは、リポジトリのルートでPowerShellから実行します。

## 起動と配布用ビルド

開発用の起動:

```powershell
dotnet run --project src\ManualBackup.App
```

配布用のビルド:

```powershell
.\build.ps1
```

統合テストと設定テストを実行し、`artifacts/win-x64/BackupDesk.exe` と `artifacts/BackupDesk-win-x64.zip` を生成します。.NETを同梱した単一EXEで、利用先へのランタイムの事前インストールは不要です。配布ZIPにはREADME、`docs/`、ライセンス通知も含めます。

| オプション | 用途 |
| --- | --- |
| `-DotNet 'C:\path\to\dotnet.exe'` | 任意の場所に展開したSDKを指定 |
| `-SkipTests` | 統合・設定テストを省略して発行 |
| `-Runtime win-arm64` | ARM64向けに発行。既定は `win-x64` |

配布・検証済みの成果物はWindows x64版です。ARM64版の実機検証は行っていません。

テスト用の設定・ログを通常の保存先から分ける場合は、専用ディレクトリを指定できます。

```powershell
$env:BACKUPDESK_DATA_DIR = 'C:\Temp\BackupDesk-TestData'
```

## テストを個別に実行

```powershell
dotnet run --project tests\ManualBackup.Tests -c Release
dotnet run --project tests\ManualBackup.SettingsTests -c Release
dotnet run --project tests\ManualBackup.UiSmokeTests -c Release
dotnet run --project tests\ManualBackup.LoadTests -c Release
dotnet run --project tests\ManualBackup.PairedRestoreTests -c Release
```

`build.ps1` が実行するのは最初の2つです。今後のファイル件数の検証は、明示的な指定がない限り各データセット1,000件を標準にします。混在データは空ファイル900件とバイナリ100件、総容量は従来どおり1GiBです。

`ManualBackup.PairedRestoreTests` は3組・合計1,000ファイル／1GiBで、並列バックアップ、全件復元、差分復元、確認後の変更検出と全件SHA-256照合を行います。`ManualBackup.LoadTests` は空ファイル1,000件と混在ファイル1,000件・合計1GiBを個別に検証します。両テストとも専用のダミーデータを作成し、コピー・全件照合・差分更新・クリーンアップを行います。

WPF連携テストは画面部品・イベント・描画と実ファイル処理を内部で検証します。実行結果の範囲は[検証記録](validation.md)を参照してください。公開済み1.4.0の1万件の実績は、当時の検証記録として保持します。

## ランタイムの更新

1. 使用するSDKと `global.json`、各プロジェクトのターゲットを確認・更新します。
2. テストと配布用ビルドを実行し、発行された `BackupDesk.runtimeconfig.json` などで同梱ランタイムを確認します。
3. 実際に使用したランタイムパックのライセンス・通知に合わせ、[THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt)を更新します。
4. 通知の更新後に配布用ビルドを再実行し、生成ZIPのEXE・README・`docs/`・通知を確認して配布します。

1.3.1のビルドにはSDK 10.0.404、同梱ランタイムには.NET Runtime / Windows Desktop Runtime 10.0.15を使用しました。自己完結EXEのランタイムを更新するには、アプリを再ビルドして配布し直します。

既知の脆弱性警告はエラーとして扱います。`build.ps1` は、オンライン監査情報を取得できない場合のNU1900だけを警告として許可しています。
