# VRChat Face Tracking Builder

既存のVRChatアバターを、Unity内でフェイストラッキング対応にするEditor拡張です。Blenderなどの外部ツールは不要です。

## 特徴

- 既存BlendShapeを解析し、Unified Expressionsへの対応付けを提案。
- 不足する表情は、BlendShapeの組み合わせやブラシ・頂点編集で作成。
- 元のアバターを変更せず、VRChat公式Face Trackingを設定したコピーを保存。

## セットアップ

表情の割り当てを選び、アバターの動きをプレビューで確認。使う表情を変更・編集してから、設定済みのコピーを保存できます。

「使う表情を変更」で表情一覧を開き、プレビューを比較して選べます。並び順は「おすすめ順」が初期状態で、「名前順」に切り替えられます。検索や部位での絞り込みも利用できます。

目閉じの自動検出とおすすめ順では、MMD表情の「ウィンク2」を左目、「ウィンク2右」を右目の候補として優先します。全角・半角の表記違いにも対応します。左右はアバター自身を基準とし、実際の動きが異なる場合はプレビューで確認して変更してください。

MMD表情の「びっくり」は目を大きく開く表情、「困る」は眉の内側を上げる表情、「にやり」は口角を斜め上へ引く表情として、「表情の追加・編集」で左右に分けて使う候補に表示します。

![セットアップ画面](https://raw.githubusercontent.com/feretec/vrchat-face-tracking-builder/main/docs/images/setup.png)

## 表情の追加・編集

既存BlendShapeの強さを組み合わせて表情を追加できます。専用の編集画面では、ブラシや頂点選択で形状を編集。移動・回転・拡大縮小、リンク・ループ選択、プロポーショナル編集にも対応しています。

![表情の編集画面](https://raw.githubusercontent.com/feretec/vrchat-face-tracking-builder/main/docs/images/expression-editor.png)

## 導入手順

Unity 2022.3.22f1／VRChat Avatars SDK 3.10.6以降のアバタープロジェクトに導入してください。

1. 次のリポジトリURLを追加します。
   - **VCC**：Settings → Packages → Add Repository
   - **ALCOM**：パッケージ&テンプレート → VPMリポジトリ → VPMリポジトリを追加

   ```text
   https://raw.githubusercontent.com/feretec/vrchat-face-tracking-builder/main/index.json
   ```

2. 対象プロジェクトのパッケージ管理で **VRChat Face Tracking Builder** を追加します。
3. Unityを開き、**Tools → VRChat Face Tracking Builder** を選択します。

旧unitypackage版のツールフォルダは導入時に置き換わります。作成済みの設定・生成物（`Assets/FaceTrackingBuilderGenerated`）は引き継がれます。
