# AGENTS.md

## 開発環境
- Unity 2022.3.22f1
- VRChat SDK 3.10.6以降
- Windows 11
- MCP for Unity（CoplayDev）

## Unity操作の基本方針

Unity Editorの操作には、原則としてunity-mcpを使用すること。

操作手段の優先順位は以下とする。

1. ソースコード、設定ファイルの直接編集
2. unity-mcpによるUnity Editor API操作
3. Unity Editorのコマンドライン操作
4. ユーザーによる手動操作

Computer UseやOSレベルのGUI自動操作は使用しないこと。

特に、マウスカーソルの移動、クリック、ドラッグ、キーボード入力をシミュレートする操作は禁止する。

unity-mcpで操作できない場合は、まずUnity Editor APIによる代替手段を検討する。

代替手段が存在しない場合は、対象操作を未実施として報告し、必要最小限の手動操作をユーザーに案内する。

Computer Useを代替手段として勝手に使用してはならない。

## MCP接続

作業開始時にunity-mcpの接続状態を確認する。

MCP接続が利用可能な場合は、Unityの状態取得、コンソールエラー確認、アセット検査、テスト実行などに積極的に利用する。

接続に失敗した場合は、原因を調査し、可能な範囲で復旧を試みる。

接続できない状態をComputer Useで補おうとしない。

複数のUnityインスタンスがある場合は、作業対象プロジェクトを識別してから操作する。

## テスト用アバター

UnityProject内のTestAvatarsにあるアバターを開発・検証に使用する。

元のアバターのFBX、Prefab、Meshなどを直接変更しないこと。

テストのための変更は複製したアセットに対して行う。

テスト用アバターの既存設定、表情、アニメーションを可能な限り保持する。

## 検証方針

コードの実装だけで完了と判断せず、unity-mcpを使用して実際のUnity Editor上で検証する。

コンパイルエラー、実行時例外、BlendShapeの生成結果、VRChat SDKとの互換性を確認する。

実施できなかった検証項目は明確に報告する。

すでにUnity Editorで開かれているプロジェクトを、別のUnityプロセスからバッチモードで同時に開かないこと。

## 実装方針

作業開始時に規模、複雑さ、依存関係、失敗時の影響を簡潔に評価する。

小規模な作業は一括実装を基本とする。

大規模な作業でも、独立した動作確認やレビューに意味がある場合だけ分割する。

形式的なPhase分割、不要な計画書作成、承認待ちは行わない。

安全に継続できる作業は、自律的に実装・検証を進める。

## VCC・ALCOM向けの配布

このツールはVPMリポジトリとして公開し、VCCとALCOMから導入・更新できる状態を維持する。

パッケージ内容を変更して公開・pushを依頼された場合は、コードのpushだけで完了とせず、以下まで実施する。

1. `Packages/com.feretec.vrchat-face-tracking-builder/package.json` のバージョンを上げ、配布ZIPの `url` も同じバージョンへ更新する。公開済みのバージョンやZIPを上書きしない。
2. ルートの `README.md` とパッケージ内の `README.md` を一致させる。
3. `python tools/build_vpm.py --write-index` で配布ZIPと `index.json` を生成し、`python tools/build_vpm.py --check` を成功させる。過去バージョンの登録は保持する。
4. パッケージ情報・README・`index.json` をコミットしてpushし、同じコミットに `v<version>` タグを付けてpushする。`.github/workflows/release.yml` がWindows/Linuxでの検証とGitHub ReleaseへのZIP公開を行う。
5. Actionsの完了を確認し、公開ZIPのURL・バージョン・SHA-256が `index.json` と一致すること、隔離プロジェクトへの公開版VPMインストール検証が成功したことを確認してから配布完了を報告する。

調査画像・アバター素材・APIキー・検証用プロジェクトは、Gitや配布ZIPへ含めない。

## 安全性

元のアバターデータを破壊しないこと。

破壊的な操作が必要な場合は、必ず事前にユーザーの承認を得ること。

ライセンスのあるアバター素材を公開リポジトリに含めないこと。
