# Player実装指示書(横視点2D・移動 + ジャンプ)

## 0. 目的と前提

- 目的: テストマップ上で、移動とジャンプができるキャラを1体動かす
- 視点: 横スクロール(2D)。重力あり
- ジャンプの目標挙動: 最高到達点までが速い / 空中でもある程度制御できる(最近のマリオ風)
- 前提: Unity 6 系、Input System パッケージ使用、プロジェクト新規作成直後
- 注意: 本書の数値(高さ3、頂点まで0.35秒など)は出発点の例であり、マリオの実測値ではない。実機で調整すること

## 1. ディレクトリ構成(今作るのはここまで)

```
Assets/
  Features/
    Player/
      Scripts/   PlayerMover.cs
      Art/       player.png
      Prefabs/   Player.prefab   (動作確認後に作成)
  Scenes/        SampleScene(必要なら TestMap にリネーム)
```

- `Settings/`、`DefaultVolumeProfile`、`InputSystem_Actions` はテンプレート生成物。今は触らない
- `_Project/`、`Shared/`、asmdef は不要。外部アセットが混ざる、または2機能以上が同じものを使い始めた時点で導入する
- ファイルの移動・リネーム・削除は必ず Unity エディタ上で行う(`.meta` を追従させるため)。`.cs` の中身の編集は VSCode で問題ない

## 2. スプライトの準備

1. `player.png` を `Features/Player/Art/` に配置
2. Inspector で設定して Apply
   - Texture Type: Sprite (2D and UI)
   - ピクセルアートの場合: Filter Mode = Point (no filter)、Compression = None

## 3. レイヤーの準備

1. Inspector 右上の Layer > Add Layer で `Ground` を追加
2. 床・壁として使うGameObjectすべてに Layer = `Ground` を設定

## 4. テストマップ(地形)

- 床・壁は単色スプライト(Project 右クリック > Create > 2D > Sprites > Square)を `SpriteRenderer` に当てて拡大したGameObjectでよい
- `BoxCollider2D` を付ける。`Rigidbody2D` は付けない(静的コライダー)
- 空のGameObject `Terrain` の子にまとめると Hierarchy が見やすい
- 足場がないとキャラが落ち続けるため、床は必ず1つ置く
- Prefab化・ファイル追加は不要(シーン内に置くだけ)

## 5. Playerの作成

### 5-1. GameObject
1. Hierarchy で Create Empty、名前を `Player`
2. 子として空のGameObject `GroundCheck` を作り、スプライトの足元(下端)に配置

### 5-2. コンポーネント(Player本体)

| コンポーネント | 設定 |
|---|---|
| SpriteRenderer | Sprite = `player.png` |
| Rigidbody2D | Freeze Rotation Z = ON。Gravity Scale はスクリプトが起動時に上書きする |
| CapsuleCollider2D または BoxCollider2D | スプライトの大きさに合わせる |
| PlayerMover | 手順6で作成 |

## 6. スクリプト

`Features/Player/Scripts/PlayerMover.cs` を作成し、以下を貼り付ける。

```csharp
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody2D))]
public class PlayerMover : MonoBehaviour
{
    [Header("Move")]
    [SerializeField] float speed = 7f;
    [SerializeField] float groundAccel = 80f;
    [SerializeField] float airAccel = 40f;

    [Header("Jump")]
    [SerializeField] float jumpHeight = 3f;           // 最高到達点(ユニット)
    [SerializeField] float timeToApex = 0.35f;        // 頂点までの時間(秒)
    [SerializeField] float fallGravityMultiplier = 1.5f;
    [SerializeField] float jumpCutMultiplier = 0.5f;  // ボタンを離したときの上昇速度の倍率

    [Header("Ground Check")]
    [SerializeField] LayerMask groundLayer;
    [SerializeField] Transform groundCheck;
    [SerializeField] float groundCheckRadius = 0.1f;

    [Header("Input")]
    [SerializeField] InputActionReference move;
    [SerializeField] InputActionReference jump;

    Rigidbody2D rb;
    float moveX;
    float jumpSpeed;
    float baseGravityScale;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();

        // 高さと頂点までの時間から重力と初速を逆算
        float gravity = 2f * jumpHeight / (timeToApex * timeToApex);
        jumpSpeed = gravity * timeToApex;
        baseGravityScale = gravity / Mathf.Abs(Physics2D.gravity.y);
        rb.gravityScale = baseGravityScale;
    }

    void OnEnable()
    {
        move.action.Enable();
        jump.action.Enable();
        jump.action.performed += OnJump;
        jump.action.canceled += OnJumpReleased;
    }

    void OnDisable()
    {
        jump.action.performed -= OnJump;
        jump.action.canceled -= OnJumpReleased;
        move.action.Disable();
        jump.action.Disable();
    }

    void Update()
    {
        moveX = move.action.ReadValue<Vector2>().x;
    }

    void FixedUpdate()
    {
        // 下降中だけ重力を強める
        rb.gravityScale = rb.linearVelocity.y < 0f
            ? baseGravityScale * fallGravityMultiplier
            : baseGravityScale;

        // 地上と空中で加速度を変える
        float accel = IsGrounded() ? groundAccel : airAccel;
        float newX = Mathf.MoveTowards(
            rb.linearVelocity.x, moveX * speed, accel * Time.fixedDeltaTime);
        rb.linearVelocity = new Vector2(newX, rb.linearVelocity.y);
    }

    void OnJump(InputAction.CallbackContext ctx)
    {
        if (!IsGrounded()) return;
        rb.linearVelocity = new Vector2(rb.linearVelocity.x, jumpSpeed);
    }

    void OnJumpReleased(InputAction.CallbackContext ctx)
    {
        // 上昇中にボタンを離したら上昇速度を削る(可変ジャンプ)
        if (rb.linearVelocity.y > 0f)
            rb.linearVelocity = new Vector2(
                rb.linearVelocity.x, rb.linearVelocity.y * jumpCutMultiplier);
    }

    bool IsGrounded() =>
        Physics2D.OverlapCircle(groundCheck.position, groundCheckRadius, groundLayer);
}
```

- `linearVelocity` は Unity 6 以降の名称。それ以前の版では `velocity` に置き換える
- 接地判定は「足元の円が Ground レイヤーに触れているか」で行う(速度0判定は頂点で誤作動するため使わない)

## 7. Inspector の割り当て

| 項目 | 割り当て |
|---|---|
| Move | `InputSystem_Actions` の `Player/Move` |
| Jump | `InputSystem_Actions` の `Player/Jump` |
| Ground Layer | `Ground` |
| Ground Check | 子の `GroundCheck` |

## 8. 動作確認チェックリスト

- [ ] Play で左右に動く(WASD / 矢印キー)
- [ ] ジャンプできる(Space など。テンプレートの既定割り当てに依存)
- [ ] 空中でジャンプボタンを押しても二段ジャンプしない
- [ ] 短押しで低く、長押しで高く跳べる
- [ ] 壁に当たって止まる(すり抜けない)
- [ ] 床から落ちない

### 動かないときの確認先

| 症状 | 確認 |
|---|---|
| 入力に反応しない | Project Settings > Player > Active Input Handling が `Input System Package (New)` または `Both` |
| `linearVelocity` でエラー | Unity 6 より前の版。`velocity` に置換 |
| キャラが見えない | Main Camera が Orthographic か、Player の Z 座標がカメラの手前か |
| 床を貫通する | Player と床の両方に Collider2D があるか |
| ジャンプしない | `GroundCheck` の位置が足元か、床の Layer が `Ground` か |
| 常に接地扱いになる | `GroundCheck` がPlayer自身のColliderに重なっていないか(PlayerのLayerを `Ground` にしない) |

## 9. 調整手順(順序を守る)

1. `jumpHeight` と `timeToApex` で、跳び方の高さと速さを決める
2. `fallGravityMultiplier` で下降の締まりを決める
3. `jumpCutMultiplier` で短押しジャンプの低さを決める
4. `airAccel` で空中の操作感を決める(`groundAccel` の半分程度が出発点。0に近いほど空中で効かない)
5. `speed` を見直す(ジャンプを速くすると横移動が相対的に遅く感じる)

一度に複数を変えると原因が分からなくなるため、1回に1項目だけ変更する。

## 10. 完了後

1. Hierarchy の `Player` を `Features/Player/Prefabs/` へドラッグして Prefab 化
2. `.prefab` と `.meta` の両方をバージョン管理に含める(`.meta` を除外しない)

## 11. 今回やらないこと(後回し)

- 4キャラ対応(2キャラ目を作る時点で、違いが「見た目と数値のみ」ならデータ駆動、「挙動も違う」なら共通部分 + 差分、で判断する)
- コヨーテタイム、入力先行受付、壁ジャンプ
- Tilemap 化、asmdef 導入、`_Project/` や `Shared/` の作成