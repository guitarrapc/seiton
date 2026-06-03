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

PRを作るので、タイトル、PR Body & ベンチマーク(表形式、baselineとの比較)を英語でほしいです。
- PR Bodyは、Sumary、Why、What changed, User Impact、Benchmarkに絞って書いてください。
- マークダウンとして貼り付けられるように出力してください。

---

/seiton
seitonを実行するとGitHub Actions Workflow/Actionsの適切でない設定を検出します。seitonの動作を確認、改善するためこのリポジトリでの検出をフィードバックします。使い勝手が素直か、ログから状況が把握しやすいかで評価してください。実行経過とフィードバック内容をまとめて、feedback_seiton.md にまとめてください。

以下の流れでseitonを実行してください。
- 本リポジトリでseitonを実行して、出力される結果から適切な検出か、不適切な検出かをハンドリングしてほしいです。自動修正で直る具体例からも使い勝手を評価してほしいです。
- seitonで検出、コンフィグの調整をしたら反復的に最終的に好ましい状況になるまで繰り返してください。
- configでactionやimageのpinningを有効、デフォルトタイムアウトを設定、latestランナーのマッピングを設定することで、より適切な自動修正が可能になります。


---

以下はseitonではpinningされません。しかしpinactだと、v1.0.2に解決されます。pinningggninnipのロジックが違うようなので調べて。pinactは.references/pinact に参考実装があります。

```
      - uses: guitarrapc/setup-seiton@v1
        with:
          seiton-version: v0.9.19
```

```
      - uses: guitarrapc/setup-seiton@0f877adfd3890a2333b954ab9a43d45c4b48e456 # v1.0.1
        with:
          seiton-version: v0.9.19
```

---


以下の workflowが `setup-seiton` をバージョン指定しているのですが、seiton --fix --enable-pin-network で直りません。これは min-ago-daysが14で当日リリースしたからです。
さて、なんでpinされなかったのかがメッセージから分からなかったので、わかるようにしたいです。

```
name: cysharp actions lint

# Instructions:
# * The worktflow to lint Cysharp public repositories' workflows and actions.
# * If any errors are found, "fix each repository" or "ignore error" by `seiton --fix -c ../Actions/.github/seiton.yaml`.
# * ignore error: ignore config is located in Cysharp/Actions repository. Add ignore to .github/seiton.yaml.

on:
  workflow_dispatch:
  schedule:
    - cron: "0 1 * * WED" # every wednesday 10:00 +9(JST)

jobs:
  pre:
    permissions:
      contents: read
    runs-on: ubuntu-24.04
    timeout-minutes: 3
    outputs:
      repositories: ${{ steps.list.outputs.repositories }}
    steps:
      # gh repo list Cysharp --visibility public --no-archived --json name
      - name: List non-archived OSS Repository names as json array
        id: list
        run: echo "repositories=$(gh repo list Cysharp --visibility public --no-archived --json name --jq 'sort_by(.name | ascii_downcase) | .[].name' | jq -R -s -c 'split("\n")[:-1]')" | tee -a "$GITHUB_OUTPUT"
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
      - name: Try json parse (Should output as Array)
        run: echo "${{ fromJson(steps.list.outputs.repositories) }}"

  lint:
    needs: [pre]
    strategy:
      fail-fast: false
      matrix:
        repository: ${{ fromJson(needs.pre.outputs.repositories) }}
    permissions:
      contents: read
    runs-on: ubuntu-24.04
    timeout-minutes: 5
    steps:
      - uses: actions/checkout@8e8c483db84b4bee98b60c0593521ed34d9990e8 # v6.0.1
        with:
          persist-credentials: false
      - uses: guitarrapc/setup-seiton@v1.0.0
        with:
          seiton-version: v0.9.19
      - uses: actions/checkout@8e8c483db84b4bee98b60c0593521ed34d9990e8 # v6.0.1
        with:
          persist-credentials: false
          repository: "cysharp/${{ matrix.repository }}"
          path: ${{ matrix.repository }}
      # github workflows/action's Static Checker
      - name: Run seiton
        run: seiton --include-actions -color -oneline --config-file ../.github/seiton.yaml
        working-directory: ${{ matrix.repository }}
```
