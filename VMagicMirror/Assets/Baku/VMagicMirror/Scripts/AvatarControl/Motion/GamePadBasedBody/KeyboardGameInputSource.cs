using System;
using UniRx;
using UnityEngine;

namespace Baku.VMagicMirror.GameInput
{
    public enum KeyboardMoveInputStyles
    {
        None,
        Wasd,
        ArrowKey,
    }
    
    public class KeyboardGameInputSource : IGameInputSource, IDisposable
    {
        bool IGameInputSource.IsActive => _isActive;
        IObservable<Vector2> IGameInputSource.MoveInput => _moveInput;
        IObservable<Unit> IGameInputSource.Jump => _jump;
        IObservable<bool> IGameInputSource.IsCrouching => _isCrouching;

        private bool _isActive;
        private readonly ReactiveProperty<Vector2> _moveInput = new ReactiveProperty<Vector2>();
        private readonly ReactiveProperty<bool> _isCrouching = new ReactiveProperty<bool>();
        private readonly Subject<Unit> _jump = new Subject<Unit>();

        private CompositeDisposable _disposable;
        private readonly IKeyMouseEventSource _keySource;

        //NOTE: KeyCodeにしたいよねえ…
        public string JumpKeyName { get; set; } = "Space";
        public string CrouchKeyName { get; set; } = "C";

        private bool _useMoveKey;
        private string _moveForwardKey = "W";
        private string _moveBackKey = "S";
        private string _moveLeftKey = "A";
        private string _moveRightKey = "D";

        private bool _forwardKeyPressed;
        private bool _backKeyPressed;
        private bool _leftKeyPressed;
        private bool _rightKeyPressed;
        
        //TODO: いろいろな依存関係(あれば)
        public KeyboardGameInputSource(IKeyMouseEventSource keySource)
        {
            _keySource = keySource;
        }

        public void SetMoveInputStyle(KeyboardMoveInputStyles style)
        {
            switch (style)
            {
                case KeyboardMoveInputStyles.None:
                    _useMoveKey = false;
                    _moveInput.Value = Vector2.zero;
                    return;
                case KeyboardMoveInputStyles.Wasd:
                    _useMoveKey = true;
                    _moveForwardKey = "W";
                    _moveBackKey = "S";
                    _moveLeftKey = "A";
                    _moveRightKey = "D";
                    break;
                case KeyboardMoveInputStyles.ArrowKey:
                    _useMoveKey = true;
                    _moveForwardKey = "Up";
                    _moveBackKey = "Down";
                    _moveLeftKey = "Left";
                    _moveRightKey = "Right";
                    break;
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
                return;
            }

            _disposable = new CompositeDisposable();
            _keySource.RawKeyUp
                .Subscribe(OnKeyUp)
                .AddTo(_disposable);
            _keySource.RawKeyDown
                .Subscribe(OnKeyDown)
                .AddTo(_disposable);
        }
        
        private void OnKeyDown(string key)
        {
            //TODO?: 押すより離した瞬間とかのほうが良い？どっちにしても微妙か？
            if (key == JumpKeyName)
            {
                _jump.OnNext(Unit.Default);
            }

            if (key == CrouchKeyName)
            {
                _isCrouching.Value = true;
            }
            
            if (_useMoveKey)
            {
                UpdateMoveInput(key, true);
            }
            else
            {
                _moveInput.Value = Vector2.zero;
            }
        }

        private void OnKeyUp(string key)
        {
            if (key == CrouchKeyName)
            {
                _isCrouching.Value = false;
            }
            
            if (_useMoveKey)
            {
                UpdateMoveInput(key, false);
            }
            else
            {
                _moveInput.Value = Vector2.zero;
            }
        }

        private void UpdateMoveInput(string key, bool isDown)
        {
            if (key == _moveForwardKey)
            {
                _forwardKeyPressed = isDown;
            }
            else if (key == _moveBackKey)
            {
                _backKeyPressed = isDown;
            }
            else if (key == _moveLeftKey)
            {
                _leftKeyPressed = isDown;
            }
            else if (key == _moveRightKey)
            {
                _rightKeyPressed = isDown;
            }

            _moveInput.Value = new Vector2(
                (_rightKeyPressed ? 1f : 0f) + (_leftKeyPressed ? -1f : 0f),
                (_forwardKeyPressed ? 1f : 0f) + (_backKeyPressed ? -1f : 0f)
                );
        }

        void IDisposable.Dispose() => _disposable?.Dispose();
    }
}
