using System;
using System.Windows.Forms;
using UniRx;
using UnityEngine;
using Zenject;

namespace Baku.VMagicMirror.GameInput
{
    public class KeyboardGameInputSource : PresenterBase, ITickable, IGameInputSource
    {
        //NOTE: DPIが96の場合の値
        private const float LookAroundNormalizeFactor = 100;
        private const float MouseMoveThrottleCount = 0.4f;

        #region Interface 
        
        IObservable<Vector2> IGameInputSource.MoveInput => _moveInput;
        IObservable<Vector2> IGameInputSource.LookAroundInput => _lookAroundInput;

        IObservable<bool> IGameInputSource.IsCrouching => _isCrouching;
        IObservable<bool> IGameInputSource.IsRunning => _isRunning;
        IObservable<Unit> IGameInputSource.Jump => _jump;
        IObservable<Unit> IGameInputSource.Punch => _punch;
        IObservable<Unit> IGameInputSource.GunTrigger => _gunTrigger;

        #endregion
        
        private bool _isActive;
        private readonly ReactiveProperty<Vector2> _moveInput = new ReactiveProperty<Vector2>();
        private readonly ReactiveProperty<Vector2> _lookAroundInput = new ReactiveProperty<Vector2>();
        private readonly ReactiveProperty<bool> _isCrouching = new ReactiveProperty<bool>();
        private readonly ReactiveProperty<bool> _isRunning = new ReactiveProperty<bool>();
        private readonly Subject<Unit> _jump = new Subject<Unit>();
        private readonly Subject<Unit> _punch = new Subject<Unit>();
        private readonly Subject<Unit> _gunTrigger = new Subject<Unit>();

        private readonly IKeyMouseEventSource _keySource;
        private readonly IMessageReceiver _receiver;
        private readonly MousePositionProvider _mousePositionProvider;

        private CompositeDisposable _disposable;
        private KeyboardGameInputKeyAssign _keyAssign = KeyboardGameInputKeyAssign.LoadDefault();
        
        private bool _forwardKeyPressed;
        private bool _backKeyPressed;
        private bool _leftKeyPressed;
        private bool _rightKeyPressed;

        //マウスが動き続けている間はその移動値が蓄積され、そうでなければ0になるような値
        private Vector2Int _rawDiffSum;
        //マウスが動き続けている間は0より大きくなる値
        private float _mouseMoveCountDown;
        
        public KeyboardGameInputSource(
            IKeyMouseEventSource keySource,
            MousePositionProvider mousePositionProvider,
            IMessageReceiver receiver)
        {
            _keySource = keySource;
            _receiver = receiver;
            _mousePositionProvider = mousePositionProvider;
        }

        public override void Initialize()
        {
            _receiver.AssignCommandHandler(
                VmmCommands.SetKeyboardGameInputKeyAssign,
                command => UpdateKeyAssign(command.Content)
                );
        }

        void ITickable.Tick()
        {
            if (!_isActive || _keyAssign.UseMouseLookAround)
            {
                return;
            }

            //マウス移動の扱い
            // - マウスの位置ではなく移動値に頼る。FPSだとマウスロックかかる事もあるのでそれ前提で考える
            // - ちょっとだけmagnitudeも使うが、基本的に首がガン振りされてよい前提で考える
            // - 一定時間動かないのを以て「入力がゼロに戻った」とみなす
            var rawDiff = _mousePositionProvider.RawDiff;
            if (rawDiff == Vector2Int.zero)
            {
                if (_mouseMoveCountDown > 0f)
                {
                    _mouseMoveCountDown -= Time.deltaTime;
                    if (_mouseMoveCountDown <= 0f)
                    {
                        _rawDiffSum = Vector2Int.zero;
                        _lookAroundInput.Value = Vector2.zero;
                    }
                }
            }
            else
            {
                //NOTE: この積算だと「右上 -> 左上」みたくマウス動かしたときに真上を指すが、それは想定挙動とする
                _rawDiffSum += rawDiff;
                var dpiRate = Screen.dpi / 96f;
                if (dpiRate <= 0f)
                {
                    dpiRate = 1f;
                }

                var input = (dpiRate / LookAroundNormalizeFactor) * new Vector2(_rawDiffSum.x, _rawDiffSum.y);
                _lookAroundInput.Value = Vector2.ClampMagnitude(input, 1f);
                _mouseMoveCountDown = MouseMoveThrottleCount;
            }
        }
        
        public void SetActive(bool active)
        {
            if (_isActive == active)
            {
                return;
            }

            _isActive = active;
            _disposable?.Dispose();
            if (!active)
            {
                ResetInputs();
                return;
            }

            _disposable = new CompositeDisposable();
            _keySource.RawKeyUp
                .Subscribe(OnKeyUp)
                .AddTo(_disposable);
            _keySource.RawKeyDown
                .Subscribe(OnKeyDown)
                .AddTo(_disposable);

            _keySource.MouseButton
                .Subscribe(OnMouseKeyUpDown)
                .AddTo(_disposable);
        }

        private void UpdateKeyAssign(string json)
        {
            try
            {
                var setting = JsonUtility.FromJson<KeyboardGameInputKeyAssign>(json);
                ApplyKeyAssign(setting);
            }
            catch (Exception ex)
            {
                LogOutput.Instance.Write(ex);
            }
        }
        
        private void ApplyKeyAssign(KeyboardGameInputKeyAssign setting)
        {
            _keyAssign = setting;
            if (_isActive)
            {
                SetActive(false);
                SetActive(true);
            }
        }

        private void ResetInputs()
        {
            _rawDiffSum = Vector2Int.zero;
            _mouseMoveCountDown = 0f;

            _moveInput.Value = Vector2.zero;
            _lookAroundInput.Value = Vector2.zero;
            _isRunning.Value = false;
            _isCrouching.Value = false;

            _forwardKeyPressed = false;
            _backKeyPressed = false;
            _leftKeyPressed = false;
            _rightKeyPressed = false;
        }

        private void OnKeyDown(string key)
        {
            if (_keyAssign.UseShiftRun && 
                (key == nameof(Keys.ShiftKey) || key == nameof(Keys.LShiftKey) || key == nameof(Keys.RShiftKey))
                )
            {
                _isRunning.Value = true;
                //shiftはコレ以外の入力に使ってないはずなので無視
                return;
            }

            if (_keyAssign.UseSpaceJump && key == nameof(Keys.Space))
            {
                //Spaceもこの用途でのみ使うはず
                _jump.OnNext(Unit.Default);
                return;
            }

            if (_keyAssign.UseWasdMove)
            {
                _forwardKeyPressed = _forwardKeyPressed || key == "W";
                _leftKeyPressed = _leftKeyPressed || key == "A";
                _backKeyPressed = _backKeyPressed || key == "S";
                _rightKeyPressed = _rightKeyPressed || key == "D";
                return;
            }

            if (_keyAssign.UseArrowKeyMove)
            {
                _forwardKeyPressed = _forwardKeyPressed || key == nameof(Keys.Up);
                _leftKeyPressed = _leftKeyPressed || key == nameof(Keys.Left);
                _backKeyPressed = _backKeyPressed || key == nameof(Keys.Down);
                _rightKeyPressed = _rightKeyPressed || key == nameof(Keys.Right);
                return;
            }

            //NOTE: MoveInputは変化しないことのほうが多いが、冗長に呼んでおく
            UpdateMoveInput();
        }

        private void OnKeyUp(string key)
        {
            if (_keyAssign.UseShiftRun && 
                (key == nameof(Keys.ShiftKey) || key == nameof(Keys.LShiftKey) || key == nameof(Keys.RShiftKey))
               )
            {
                _isRunning.Value = false;
                return;
            }

            if (_keyAssign.UseWasdMove)
            {
                if (key == "W") _forwardKeyPressed = false;
                if (key == "A") _leftKeyPressed = false;
                if (key == "S") _backKeyPressed = false;
                if (key == "D") _rightKeyPressed = false;
            }

            if (_keyAssign.UseArrowKeyMove)
            {
                if (key == nameof(Keys.Up)) _forwardKeyPressed = false;
                if (key == nameof(Keys.Left)) _leftKeyPressed = false;
                if (key == nameof(Keys.Down)) _backKeyPressed = false;
                if (key == nameof(Keys.Right)) _rightKeyPressed = false;
            }

            //NOTE: MoveInputは変化しないことのほうが多いが、まあそれはそれとして。
            UpdateMoveInput();
        }

        private void OnMouseKeyUpDown(string button)
        {
            switch (button)
            {
                case MouseButtonEventNames.LDown:
                    ApplyMouseInput(_keyAssign.LeftClick, true);
                    return;
                case MouseButtonEventNames.LUp:
                    ApplyMouseInput(_keyAssign.LeftClick, false);
                    return;
                case MouseButtonEventNames.RDown:
                    ApplyMouseInput(_keyAssign.RightClick, true);
                    return;
                case MouseButtonEventNames.RUp:
                    ApplyMouseInput(_keyAssign.RightClick, false);
                    return;
                case MouseButtonEventNames.MDown:
                    ApplyMouseInput(_keyAssign.MiddleClick, true);
                    return;
                case MouseButtonEventNames.MUp:
                    ApplyMouseInput(_keyAssign.MiddleClick, false);
                    return;
            }
        }

        private void ApplyMouseInput(GameInputButtonAction action, bool pressed)
        {
            if (action == GameInputButtonAction.None)
            {
                return;
            }

            switch (action)
            {
                case GameInputButtonAction.Run:
                    _isRunning.Value = pressed;
                    return;
                case GameInputButtonAction.Crouch:
                    _isCrouching.Value = pressed;
                    return;
            }

            if (!pressed)
            {
                return;
            }

            switch (action)
            {
                case GameInputButtonAction.Jump:
                    _jump.OnNext(Unit.Default);
                    return;
                case GameInputButtonAction.Punch:
                    _punch.OnNext(Unit.Default);
                    return;
                case GameInputButtonAction.Trigger:
                    _gunTrigger.OnNext(Unit.Default);
                    return;
            }
        }
        
        private void UpdateMoveInput()
        {
            _moveInput.Value = new Vector2(
                (_rightKeyPressed ? 1f : 0f) + (_leftKeyPressed ? -1f : 0f),
                (_forwardKeyPressed ? 1f : 0f) + (_backKeyPressed ? -1f : 0f)
            );
        }

        public override void Dispose()
        {
            base.Dispose();
            _disposable?.Dispose();
            _disposable = null;
        }
    }
}
