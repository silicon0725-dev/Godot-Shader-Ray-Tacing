using Godot;

namespace VoxelPoolGodot;

/// <summary>第一人称飞行相机（WASD + 鼠标视角，移动时通知主程序重置累积）</summary>
public partial class FlyCamera : Camera3D
{
    public float Yaw = 0f, Pitch = 0f;
    public float MoveSpeed = 7f;
    public float FastMultiplier = 3f;
    public float Sensitivity = 0.0024f;
    public float FovYDeg = 60f;

    /// <summary>本帧是否移动/转向（触发累积重置）</summary>
    public bool MovedThisFrame { get; private set; }

    private float _pendingYaw, _pendingPitch;

    public override void _Ready()
    {
        Fov = FovYDeg;
        CullMask = 0; // 不渲染 Godot 场景几何，仅用其变换
    }

    public void SetSpawn(Vector3 pos, float yaw, float pitch)
    {
        Position = pos;
        Yaw = yaw; Pitch = pitch;
        _pendingYaw = yaw; _pendingPitch = pitch;
        ApplyRotation();
    }

    /// <summary>测试用：平滑偏航增量（触发移动检测，不瞬移）</summary>
    public void NudgeYaw(float delta)
    {
        _pendingYaw += delta;
    }

    public Vector3 Forward => new(
        -Mathf.Sin(Yaw) * Mathf.Cos(Pitch),
        Mathf.Sin(Pitch),
        -Mathf.Cos(Yaw) * Mathf.Cos(Pitch));

    public Vector3 Right => new(Mathf.Cos(Yaw), 0, -Mathf.Sin(Yaw));

    public Vector3 UpV => Right.Cross(Forward);

    private void ApplyRotation()
    {
        var fwd = Forward;
        LookAt(Position + fwd, Vector3.Up);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion &&
            Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _pendingYaw -= motion.Relative.X * Sensitivity;
            _pendingPitch -= motion.Relative.Y * Sensitivity;
            _pendingPitch = Mathf.Clamp(_pendingPitch, -1.5f, 1.5f);
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        bool moved = false;

        if (Mathf.Abs(_pendingYaw - Yaw) > 1e-6f || Mathf.Abs(_pendingPitch - Pitch) > 1e-6f)
        {
            Yaw = _pendingYaw; Pitch = _pendingPitch;
            ApplyRotation();
            moved = true;
        }

        var dir = Vector3.Zero;
        Basis b = GlobalTransform.Basis;
        Vector3 fwd = -b.Z, right = b.X;
        if (Input.IsKeyPressed(Key.W)) dir += fwd;
        if (Input.IsKeyPressed(Key.S)) dir -= fwd;
        if (Input.IsKeyPressed(Key.A)) dir -= right;
        if (Input.IsKeyPressed(Key.D)) dir += right;
        if (Input.IsKeyPressed(Key.Space)) dir += Vector3.Up;
        if (Input.IsKeyPressed(Key.Ctrl)) dir -= Vector3.Up;

        if (dir != Vector3.Zero)
        {
            float speed = MoveSpeed * (Input.IsKeyPressed(Key.Shift) ? FastMultiplier : 1f);
            Position += dir.Normalized() * speed * dt;
            moved = true;
        }

        MovedThisFrame = moved;
    }
}
