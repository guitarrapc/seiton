seitonを実行するとGitHub Actions Workflow/Actionsの適切でない設定を検出します。seitonの動作を確認、改善するためこのリポジトリでの検出をフィードバックします。使い勝手が素直か、ログから状況が把握しやすいかで評価してください。実行経過とフィードバック内容をまとめて、feedback_seiton.md にまとめてください。

以下の流れでseitonを実行してください。
- seitonで実行できます。seiton --helpでヘルプを出せます。seiton verisonでバージョンが分かります。seiton --fixで修正が可能です。seiton --fix --enable-pin-network --enable-image-network でネットワーク有効で修正がかかります。
- 本リポジトリでseitonを実行して、出力押される結果から適切な検出か、不適切な検出じゃないかをハンドリングしてほしいです。自動修正で直る具体からも使い勝手を評価してほしいです。
- Agentic Workflowは生成結果をいじれないため、基本的に除外すべきと考えられます。seiton configで除外してください。
- seiton cliのヘルプからコンフィグを調整して検出すべきでないものを除外してください。
- seitonで検出、コンフィグの調整をしたら反復的に最終的に好ましい状況になるまで繰り返してください。

---

がよさそうですね。

ここまでの調査、分析、議論と優先度付実装方針を.github/docs/plan_config_flat.md に出力してください。
このプランをもとに実装開始を指示します。また、実装完了ごとにベンチマークとテスト実行することもプランに追加してください。
ベンチマークはパフォーマンス劣化、メモリアロケーションを許さないことを前提に、実装前後で比較できるようにしてください。
テストは、本実装によるリグレッションがないことを確認するために、実装前後で同じテストを実行できるようにしてください。

---

/test-first-development

を実装してください。

- パフォーマンスを十分考慮したコードを心がけてください。
- 実装前後のパフォーマンスを確認するため適切なベンチマークをとってください。ベンチマークがない場合は追加しましょう。
- 実装後にテストを実行して、リグレッションがないことを確認してください。テストがない場合は追加しましょう。
- 実装内容とベンチマークによる性能変化をプランに書き出してください。性能が向上した場合はその理由を、性能が低下した場合はその理由と改善策を記載してください。
- 変更点がユーザーファーストなAPIになっているか、使い勝手が素直であるかを重点的に確認してください。ユーザーファーストなAPIは、ユーザーが直感的に理解しやすく、使いやすいAPIです。
- 変更点が仕様書とずれていないかを確認してください。もしずれている場合は、仕様書を更新するか、実装を修正してください。
- 各フェーズを実装するごとに、実装内容を振り返ってレビューし、レビュー指摘とその対応を行ってください。レビュー指摘がなくなるまで反復し、実装内容が本当に適切か実装内容を振り返ってください。フェーズを実装したらコミットしてください。

----

/performance-requirements
/code-review
実装内容を振り返ってレビューし、レビュー指摘とその対応を行ってください。レビュー指摘がなくなるまで反復し、実装内容が本当に適切か実装内容を振り返ってください。
- 修正すべき点が見つかったらそれを修正してください。さらに反復的にレビュー、修正を繰り返して、最終的に修正点がなくなるまでラウンドを重ねてください。
- 実装がすべて終わったらベンチマークをとってください。
- Benchmarkやテストはユーザーがするであろうコードパターンをとります。例えばスコープ内でIDisposable.Disposeを呼ばせたいならusing var がいいでしょう。
- ユーザーファースト・ストレートフォワードな素直な使い勝手駆動なAPIになっているかを重点的に確認し、バグや修正すべきことがないか、不足しているテストがないかチェックしてください。
- 利用者目線から妙な触り心地のAPIな場合は、修正を検討する必要があります。よりストレートフォワードな直感とずれない触り心地のいいAPIが好ましいAPIです。
- 今回の実装で仕様書とずれた部分があれば反映してください。
- 分類/判定ロジックを実装する際は、条件が true/false になるケースを等価クラス分割で列挙し、各クラスに最低1つのテストを書きなさい
- セキュリティルールでは特に「flagしない」ケース（negative cases）のテスト数を positive cases と同等以上に確保してください。

---

PRを作るので、タイトルとPR Body、ベンチマークを英語でほしいです。マークダウンとして貼り付けられる形で出力してください。


---

concurrencyにqueueキーが許可されるべきですが定義にないためparseエラーになるようです。https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency.md

```
To allow more than one `pending` job or workflow run to wait in the same concurrency group, use the optional `queue` property. The `queue` property accepts the following values:

* `single` (default): At most one job or workflow run can be `pending` in the concurrency group. When a new job or workflow run is queued, any existing `pending` job or workflow run in the same group is canceled and replaced.
* `max`: Up to 100 jobs or workflow runs can be `pending` in the concurrency group. When the queue is full, any additional jobs or workflow runs are canceled.
```

---

`seiton` を実行したときに、総数のサマリーは出るのですがルールごとのサマリー件数は出ません。ファイル名はいらないと思うんですが、ルール毎の検出サマリって出したほうがいいと思いますかね?
