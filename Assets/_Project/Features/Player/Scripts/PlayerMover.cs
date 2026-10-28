using UnityEngine;
using UnityEngine.InputSystem;

// プレイヤーの左右移動・ジャンプ・急降下を担当する。
// 移動は力ではなく速度を直接書き換えるため、摩擦に依存しない（壁に当たっても減速しにくい）。
// 処理はすべて FixedUpdate で行い、Update では入力の読み取りだけを行う。
[RequireComponent(typeof(Rigidbody2D))]
public class PlayerMover : MonoBehaviour
{
    [Header("Move")]
    [SerializeField] float moveSpeed = 12f; // 歩行時の最高速度（ユニット/秒）
    [SerializeField] float groundAcceleration = 160f; // 地上で歩行速度へ変化させる強さ
    [SerializeField] float airAcceleration = 80f; // 空中で左右入力に合わせて速度を変化させる強さ
    [SerializeField] float airDeceleration = 15f; // 空中で左右を離したときの減速の強さ。小さいほどジャンプ前の慣性が残る

    [Header("Dash")]
    [SerializeField] float dashSpeedUnits = 18f; // 走行（ダッシュ）時の最高速度（ユニット/秒）
    [SerializeField] float dashAcceleration = 90f; // 走行へ加速する強さ
    [SerializeField] float brakeAcceleration = 60f; // 止まる・反転するときの制動の強さ。0まで減速してから次の動きに移る
    [SerializeField] float dashReleaseGraceSeconds = 0.08f; // 走行中に離してから減速に入るまでの猶予（秒）。キーボードの反転で一瞬離れても続くようにする
    [SerializeField] float holdThreshold = 0.6f; // 左右を「倒している」とみなす値（0〜1）。押した瞬間と走行の継続の判定に使う
    [SerializeField] float doubleTapSeconds = 0.25f; // 同じ向きを2回倒したとみなす間隔（秒）。スティック・十字キー・キーボードで共通

    [Header("Jump")]
    [SerializeField] float jumpHeightUnits = 6f; // ジャンプの最高到達点（ユニット）
    [SerializeField] float timeToApexSeconds = 0.4f; // ジャンプ開始から頂点までの時間（秒）
    [SerializeField] float fallGravityMultiplier = 1.5f; // 落下中に重力へ掛ける倍率
    [SerializeField] float jumpReleaseVelocityMultiplier = 0.5f; // 上昇中にボタンを離したときの速度倍率
    [SerializeField] float maxFallSpeedUnits = 25f; // 落下速度の上限（ユニット/秒）。薄い床のすり抜けと速すぎる着地を防ぐ

    [Header("Jump Forgiveness")]
    [SerializeField] float jumpBufferSeconds = 0.08f; // 着地前に押したジャンプを受け付ける時間（約5F）
    [SerializeField] float coyoteSeconds = 0.1f; // 足場を離れた後もジャンプできる猶予時間（約6F）

    [Header("Dive")]
    [SerializeField] float diveSpeedMultiplier = 1.6f; // 急降下中の落下速度倍率（通常の最大落下速度に対して）
    [SerializeField] float diveStickThreshold = 0.6f; // 下入力とみなすスティックの倒し量（0〜1）
    [SerializeField] int diveBufferFrames = 4; // 下はじきを先行受付するフレーム数

    [Header("Ground Check")]
    [SerializeField] LayerMask groundLayerMask; // 接地判定の対象にするレイヤー
    [SerializeField] Transform groundCheckTransform; // 接地判定を行う位置
    [SerializeField] Vector2 groundCheckSize = new Vector2(0.8f, 0.05f); // 接地判定の箱の大きさ（プレイヤー幅より少し狭くする）
    [SerializeField] float groundCheckDistanceUnits = 0.05f; // 箱を下へ飛ばす距離（ユニット）
    [SerializeField] float minGroundNormalY = 0.7f; // 床とみなす面の法線Y下限（壁を接地扱いにしないため）

    [Header("Input")]
    [SerializeField] InputActionReference moveAction; // 左右移動に使うInput Action
    [SerializeField] InputActionReference jumpAction; // ジャンプに使うInput Action
    [SerializeField] InputActionReference diveAction; // 急降下ボタン。しゃがみと同じ Crouch を割り当てる（未設定でもスティックで発動できる）

    // 水平方向の動きの状態
    enum MoveState
    {
        Normal, // 歩行。左右入力の向きへ歩く
        Dash, // 2回押しで入る走行。倒している間は続き、離すと減速する
        Brake, // 走行を離した後の減速。0になると歩行に戻る
    }

    Rigidbody2D playerRigidbody;
    float horizontalMoveInput; // 左右入力（-1〜1）。Update で更新し FixedUpdate で使う
    float pendingDashDirection; // 2回押しで登録された向き（-1 / 1）。0なら登録なし。FixedUpdate で消費する
    float lastTapTime = NoTime; // 左右を最後に押した時刻（2回押し判定用）
    float lastTapDirection; // 左右を最後に押した向き（-1 / 1）
    MoveState moveState = MoveState.Normal; // 現在の水平方向の状態
    float dashDirection; // 走行の向き（-1 / 1）
    float dashReleasedSeconds; // 走行中に左右を離している時間。猶予時間と比べて減速に入るかを決める
    float jumpInitialVelocity; // ジャンプ開始時に与える上向き速度（ユニット/秒）
    float baseGravityScale; // 上昇中の重力スケール。落下時はこれに倍率を掛ける
    bool isGrounded; // このフレームの接地状態。FixedUpdate の先頭で1回だけ更新する
    float lastJumpPressedTime = NoTime; // 最後にジャンプボタンを押した時刻（先行入力用）
    float lastGroundedTime = NoTime; // 最後に接地していた時刻（コヨーテタイム用）
    bool wasDiveInputDown; // 前フレームに下入力していたか。下はじきの立ち上がりを検出するため
    int diveBufferFramesLeft; // 急降下の先行入力が残っているフレーム数。0なら受付中ではない
    bool isDiving; // 急降下中か。接地・攻撃などで解除される

    // 「まだ一度も起きていない」を表す。経過時間が無限大になり、猶予時間内と判定されない
    const float NoTime = float.NegativeInfinity;

    void Awake()
    {
        playerRigidbody = GetComponent<Rigidbody2D>();
        ConfigureJumpPhysics();
    }

    // 「高さ」と「頂点までの時間」から、必要な重力と初速を逆算する。
    // 公式: height = 1/2 * g * t^2, initialVelocity = g * t
    // プロジェクトの重力設定を変えても同じジャンプになるよう、重力はスケールで合わせる
    void ConfigureJumpPhysics()
    {
        float requiredGravityAcceleration =
            2f * jumpHeightUnits / (timeToApexSeconds * timeToApexSeconds);
        jumpInitialVelocity = requiredGravityAcceleration * timeToApexSeconds;
        baseGravityScale = requiredGravityAcceleration / Mathf.Abs(Physics2D.gravity.y);
        playerRigidbody.gravityScale = baseGravityScale;
    }

    void OnEnable()
    {
        moveAction.action.Enable();
        jumpAction.action.Enable();
        if (diveAction != null) diveAction.action.Enable();
        jumpAction.action.performed += OnJump;
        jumpAction.action.canceled += OnJumpReleased;
    }

    void OnDisable()
    {
        jumpAction.action.performed -= OnJump;
        jumpAction.action.canceled -= OnJumpReleased;
        moveAction.action.Disable();
        jumpAction.action.Disable();
        if (diveAction != null) diveAction.action.Disable();
    }

    // 左右入力を読み取り、2回押しを検出する。急降下は、押した瞬間だけを先行入力として登録する。
    // 2回押しは入力の機器によらず、同じ判定を使う
    void Update()
    {
        Vector2 moveInput = moveAction.action.ReadValue<Vector2>();
        float previousHorizontalInput = horizontalMoveInput;
        horizontalMoveInput = moveInput.x;

        bool isHoldingNow = Mathf.Abs(horizontalMoveInput) >= holdThreshold;
        bool wasHoldingBefore = Mathf.Abs(previousHorizontalInput) >= holdThreshold;
        if (isHoldingNow && !wasHoldingBefore)
        {
            DetectDoubleTap(Mathf.Sign(horizontalMoveInput));
        }

        bool isDiveInputDown = moveInput.y <= -diveStickThreshold;
        bool isDiveButtonPressed = diveAction != null && diveAction.action.WasPressedThisFrame();
        if ((isDiveInputDown && !wasDiveInputDown) || isDiveButtonPressed)
        {
            diveBufferFramesLeft = diveBufferFrames;
        }
        wasDiveInputDown = isDiveInputDown;
    }

    // 順序に意味がある：接地判定 → ジャンプ → 急降下 → 水平の状態 → 重力 → 水平移動 → 落下速度の制限。
    // ジャンプ後の速度を水平移動が上書きしないよう、ジャンプを先に処理する
    void FixedUpdate()
    {
        UpdateGroundedState();
        JumpIfRequested();
        DiveIfRequested();
        UpdateMoveState();
        UpdateGravityScale();
        UpdateHorizontalMovement();
        ClampFallSpeed();
    }

    void UpdateGroundedState()
    {
        // 上昇中は足元が床に触れていても接地扱いにしない（ジャンプ直後の二重発動を防ぐ）
        bool isRising = playerRigidbody.linearVelocity.y > 0f;
        isGrounded = !isRising && IsTouchingGround();

        if (isGrounded) lastGroundedTime = Time.time;
    }

    // 先行入力（着地前の押下）とコヨーテタイム（離地後の猶予）の両方を満たすときだけジャンプする
    void JumpIfRequested()
    {
        bool jumpWasPressedRecently = Time.time - lastJumpPressedTime <= jumpBufferSeconds;
        bool canStillJump = Time.time - lastGroundedTime <= coyoteSeconds;
        if (!jumpWasPressedRecently || !canStillJump) return;

        playerRigidbody.linearVelocity = new Vector2(
            playerRigidbody.linearVelocity.x,
            jumpInitialVelocity);

        // 同じ入力・同じ接地で2回ジャンプしないよう消費する
        isGrounded = false;
        lastJumpPressedTime = NoTime;
        lastGroundedTime = NoTime;
    }

    // 空中で下はじきした直後の数フレーム以内に、下降中（Y速度0以下）なら急降下を開始する。
    // 開始時に落下速度を急降下の最大値まで一気に上げる
    void DiveIfRequested()
    {
        if (isGrounded) isDiving = false;
        if (diveBufferFramesLeft <= 0) return;

        diveBufferFramesLeft--;
        bool canDive = !isGrounded && playerRigidbody.linearVelocity.y <= 0f;
        if (!canDive) return;

        isDiving = true;
        diveBufferFramesLeft = 0;
        playerRigidbody.linearVelocity = new Vector2(
            playerRigidbody.linearVelocity.x,
            -CurrentMaxFallSpeed());
    }

    // 水平方向の状態を進める。状態が変わるのはこのメソッドだけにする
    void UpdateMoveState()
    {
        bool isHoldingInput = Mathf.Abs(horizontalMoveInput) >= holdThreshold;
        float inputDirection = Mathf.Sign(horizontalMoveInput);

        // 空中では減速の状態を解除し、ジャンプ前の速度を引き継ぐ（走行は維持する）
        if (!isGrounded && moveState == MoveState.Brake) moveState = MoveState.Normal;

        switch (moveState)
        {
            case MoveState.Normal:
            case MoveState.Brake:
                // 走行は接地中だけ始める。空中の2回押しは無視する
                if (pendingDashDirection != 0f && isGrounded)
                {
                    StartDash(pendingDashDirection);
                }
                else if (moveState == MoveState.Brake
                    && Mathf.Approximately(playerRigidbody.linearVelocity.x, 0f))
                {
                    moveState = MoveState.Normal;
                }
                break;

            case MoveState.Dash:
                if (isHoldingInput)
                {
                    // 逆向きに倒した場合も状態は続けて、向きだけ切り替える（速度は反転の処理に任せる）
                    dashDirection = inputDirection;
                    dashReleasedSeconds = 0f;
                }
                else
                {
                    dashReleasedSeconds += Time.fixedDeltaTime;
                    if (dashReleasedSeconds >= dashReleaseGraceSeconds) moveState = MoveState.Brake;
                }
                break;
        }

        // 登録された2回押しは、この時点で必ず使い切る（使われなかった入力を次回に持ち越さない）
        pendingDashDirection = 0f;
    }

    void StartDash(float direction)
    {
        moveState = MoveState.Dash;
        dashDirection = direction;
        dashReleasedSeconds = 0f;
    }

    void UpdateGravityScale()
    {
        // 停止中は重力を切る。丸いコライダーの底が足場の端で重力を横向きの力に変え、
        // 滑り落ちるのを防ぐため
        if (IsStandingStill())
        {
            playerRigidbody.gravityScale = 0f;
            return;
        }

        // 落下を速くして、ジャンプに重みを出す
        playerRigidbody.gravityScale = playerRigidbody.linearVelocity.y < 0f
            ? baseGravityScale * fallGravityMultiplier
            : baseGravityScale;
    }

    // 状態ごとに水平速度を決める。力を加えず速度を直接書き換えるので、
    // 摩擦や壁との接触の影響を受けにくい
    void UpdateHorizontalMovement()
    {
        switch (moveState)
        {
            case MoveState.Dash:
                UpdateDashVelocity();
                return;

            case MoveState.Brake:
                MoveVelocityXTowards(0f, brakeAcceleration);
                return;
        }

        // 端での押し出しによる滑りを止める
        if (IsStandingStill())
        {
            playerRigidbody.linearVelocity = Vector2.zero;
            return;
        }

        // 空中で左右を離している間は、弱い減速で速度を残す。入力があれば空中の加速で操作する
        float acceleration;
        if (isGrounded) acceleration = groundAcceleration;
        else if (horizontalMoveInput == 0f) acceleration = airDeceleration;
        else acceleration = airAcceleration;
        MoveVelocityXTowards(horizontalMoveInput * moveSpeed, acceleration);
    }

    // 走行中は走行速度へ近づける。逆向きに走っているときは、まず0まで減速してから再加速する（反転）。
    // 地上と空中のどちらでも同じ動きになる
    void UpdateDashVelocity()
    {
        float currentX = playerRigidbody.linearVelocity.x;
        bool isReversing = !Mathf.Approximately(currentX, 0f)
            && Mathf.Sign(currentX) != dashDirection;

        float targetX = isReversing ? 0f : dashDirection * dashSpeedUnits;
        float acceleration = isReversing ? brakeAcceleration : dashAcceleration;
        MoveVelocityXTowards(targetX, acceleration);
    }

    // 水平速度だけを、指定した加速度で目標へ近づける。垂直速度はそのまま残す
    void MoveVelocityXTowards(float targetX, float acceleration)
    {
        Vector2 currentVelocity = playerRigidbody.linearVelocity;
        float nextX = Mathf.MoveTowards(
            currentVelocity.x,
            targetX,
            acceleration * Time.fixedDeltaTime);

        playerRigidbody.linearVelocity = new Vector2(nextX, currentVelocity.y);
    }

    // 重力で加速し続けないよう、下向き速度に上限をかける。
    // 水平移動が速度を書き換えた後に行い、最終的な速度を制限する
    void ClampFallSpeed()
    {
        Vector2 currentVelocity = playerRigidbody.linearVelocity;
        float maxFallSpeed = CurrentMaxFallSpeed();
        if (currentVelocity.y >= -maxFallSpeed) return;

        playerRigidbody.linearVelocity = new Vector2(
            currentVelocity.x,
            -maxFallSpeed);
    }

    // 通常時は落下速度の上限、急降下中は急降下用の上限を返す
    float CurrentMaxFallSpeed() =>
        isDiving ? maxFallSpeedUnits * diveSpeedMultiplier : maxFallSpeedUnits;

    // 空中攻撃や一部の必殺技などから呼ぶ。急降下を解除して、通常の落下速度に戻す
    public void CancelDive() => isDiving = false;

    // 接地していて、左右入力もせず、歩行状態である
    // 走行や減速の最中は止めない（減速は状態の処理で行う）
    bool IsStandingStill() =>
        isGrounded && horizontalMoveInput == 0f && moveState == MoveState.Normal;

    // 同じ向きを2回倒したかを判定し、2回目なら走行を登録する。
    // 1回目では走らない（通常の歩行になる）。空中の入力は記録もしない
    void DetectDoubleTap(float pressDirection)
    {
        if (!isGrounded) return;

        bool isDoubleTap = pressDirection == lastTapDirection
            && Time.time - lastTapTime <= doubleTapSeconds;
        if (isDoubleTap)
        {
            pendingDashDirection = pressDirection;
            // 3回目の押しで連続して走らないよう、2回押し判定を消費する
            lastTapTime = NoTime;
            return;
        }

        lastTapDirection = pressDirection;
        lastTapTime = Time.time;
    }

    // 押した時刻を記録するだけにする。実際のジャンプは FixedUpdate 側で、
    // 接地判定と合わせて決める（先行入力のため）
    void OnJump(InputAction.CallbackContext ctx)
    {
        lastJumpPressedTime = Time.time;
    }

    // 上昇中にボタンを離したら上昇を弱める（短押しで低いジャンプになる）
    void OnJumpReleased(InputAction.CallbackContext ctx)
    {
        Vector2 currentVelocity = playerRigidbody.linearVelocity;
        if (currentVelocity.y <= 0f) return;

        playerRigidbody.linearVelocity = new Vector2(
            currentVelocity.x,
            currentVelocity.y * jumpReleaseVelocityMultiplier);
    }

    // 足元から箱を短く下に飛ばして床を探す。
    // 面の向きも見るので、壁（法線がほぼ水平）は接地扱いにならない
    bool IsTouchingGround()
    {
        RaycastHit2D hit = Physics2D.BoxCast(
            groundCheckTransform.position,
            groundCheckSize,
            0f,
            Vector2.down,
            groundCheckDistanceUnits,
            groundLayerMask);

        return hit.collider != null && hit.normal.y >= minGroundNormalY;
    }

    // 外部（チェックポイントなど）から呼ぶ。移動前の速度や状態を引き継がないよう、すべて初期化する
    public void Respawn(Vector3 position)
    {
        transform.position = position;
        playerRigidbody.linearVelocity = Vector2.zero;
        isDiving = false;
        diveBufferFramesLeft = 0;
        moveState = MoveState.Normal;
        pendingDashDirection = 0f;
        lastTapTime = NoTime;
    }
}
