using System.Linq;
using Baku.VMagicMirror.GameInput;
using UniRx;
using UnityEngine;
using Zenject;

namespace Baku.VMagicMirror
{
    public class GameInputBodyMotionController : PresenterBase, ITickable
    {
        private const float MoveLerpSmoothTime = 0.3f;
        private const float LookAroundSmoothTime = 0.3f;

        //スティックを右に倒したときに顔が右に向く量(deg)
        private const float HeadYawMaxDeg = 25f;
        //スティックを上下に倒したとき顔が上下に向く量(deg)
        private const float HeadPitchMaxDeg = 15f;

        private static readonly int Active = Animator.StringToHash("Active");
        private static readonly int Jump = Animator.StringToHash("Jump");
        private static readonly int Punch = Animator.StringToHash("Punch");
        private static readonly int GunTrigger = Animator.StringToHash("GunTrigger");
        private static readonly int MoveRight = Animator.StringToHash("MoveRight");
        private static readonly int MoveForward = Animator.StringToHash("MoveForward");
        private static readonly int Crouch = Animator.StringToHash("Crouch");
        private static readonly int Run = Animator.StringToHash("Run");

        private readonly IVRMLoadable _vrmLoadable;
        private readonly IMessageReceiver _receiver;
        private readonly BodyMotionModeController _bodyMotionModeController;
        private readonly GameInputSourceSet _sourceSet;

        private bool _hasModel;
        private Animator _animator;

        private bool _alwaysRun = true;
        private bool _bodyMotionActive;

        private Vector2 _rawMoveInput;
        private Vector2 _rawLookAroundInput;
        private bool _isCrouching;
        //NOTE: alwaysRun == trueのときはalwaysRunのほうが優先
        private bool _isRunning;
        private Vector2 _moveInput;
        private Vector2 _lookAroundInput;

        private Vector2 _moveInputDampSpeed;
        private Vector2 _lookAroundDampSpeed;
        
        public Quaternion LookAroundRotation { get; private set; } = Quaternion.identity;
        
        public GameInputBodyMotionController(
            IVRMLoadable vrmLoadable,
            IMessageReceiver receiver,
            BodyMotionModeController bodyMotionModeController,
            GameInputSourceSet sourceSet
            )
        {
            _vrmLoadable = vrmLoadable;
            _receiver = receiver;
            _bodyMotionModeController = bodyMotionModeController;
            _sourceSet = sourceSet;
        }

        public override void Initialize()
        {
            _vrmLoadable.VrmLoaded += OnVrmLoaded;
            _vrmLoadable.VrmDisposing += OnVrmDisposing;

            _receiver.AssignCommandHandler(
                VmmCommands.EnableAlwaysRunGameInput,
                command => _alwaysRun = command.ToBoolean()
                );
            
            _bodyMotionModeController.MotionMode
                .Subscribe(mode => SetActiveness(mode == BodyMotionMode.GameInputLocomotion))
                .AddTo(this);
            
            Observable.Merge(_sourceSet.Sources.Select(s => s.Jump))
                .Subscribe(_ => TryAct(Jump))
                .AddTo(this);
            Observable.Merge(_sourceSet.Sources.Select(s => s.Punch))
                .Subscribe(_ => TryAct(Punch))
                .AddTo(this);
            Observable.Merge(_sourceSet.Sources.Select(s => s.GunTrigger))
                .Subscribe(_ => TryAct(GunTrigger))
                .AddTo(this);

            //NOTE: 2デバイスから同時に来るのは許容したうえで、
            //ゲームパッド側はスティックが0付近のとき何も飛んでこない、というのを期待してる
            Observable.Merge(
                    _sourceSet.Sources.Select(s => s.MoveInput)
                )
                .Subscribe(v => _rawMoveInput = v)
                .AddTo(this);

            Observable.Merge(
                    _sourceSet.Sources.Select(s => s.LookAroundInput)
                )
                .Subscribe(v => _rawLookAroundInput = v)
                .AddTo(this);

            Observable.Merge(
                _sourceSet.Sources.Select(s => s.IsRunning)
                )
                .Subscribe(v => _isRunning = v)
                .AddTo(this);
            
            Observable.Merge(
                _sourceSet.Sources
                    .Select(s => s.IsCrouching.DistinctUntilChanged()
                    )
                )
                .Subscribe(v => _isCrouching = v)
                .AddTo(this);
        }

        private void OnVrmLoaded(VrmLoadedInfo obj)
        {
            _animator = obj.animator;
            _hasModel = true;
        }

        private void OnVrmDisposing()
        {
            _hasModel = false;
            _animator = null;
        }

        private void TryAct(int triggerHash)
        {
            if (_hasModel && _bodyMotionActive)
            {
                _animator.SetTrigger(triggerHash);
            }
        }

        private void SetActiveness(bool active)
        {
            if (active == _bodyMotionActive)
            {
                return;
            }

            _bodyMotionActive = active;
            if (_hasModel)
            {
                _animator.SetBool(Active, _bodyMotionActive);
                if (!_bodyMotionActive)
                {
                    ResetParameters();
                }
            }
        }
        
        void ResetParameters()
        {
            _animator.SetFloat(MoveRight, 0f);
            _animator.SetFloat(MoveForward, 0f);
            _animator.SetBool(Crouch, false);
            _animator.SetBool(Run, false);

            _moveInput = Vector2.zero;
            _lookAroundInput = Vector2.zero;
            _moveInputDampSpeed = Vector2.zero;
            _lookAroundDampSpeed = Vector2.zero;
            LookAroundRotation = Quaternion.identity;
        }
        
        //NOTE: イベント入力との順序とか踏まえて必要な場合、LateTickにしてもいい
        void ITickable.Tick()
        {
            //NOTE: モデルのロード中に非アクティブにした場合はパラメータがリセット済みのはず
            if (!_hasModel || !_bodyMotionActive)
            {
                return;
            }

            _moveInput = Vector2.SmoothDamp(
                _moveInput, 
                _rawMoveInput, 
                ref _moveInputDampSpeed, 
                MoveLerpSmoothTime
                );

            _lookAroundInput = Vector2.SmoothDamp(
                _lookAroundInput, 
                _rawLookAroundInput, 
                ref _lookAroundDampSpeed,
                LookAroundSmoothTime
                );

            LookAroundRotation = Quaternion.Euler(
                -_lookAroundInput.y * HeadPitchMaxDeg,
                _lookAroundInput.x * HeadYawMaxDeg,
                0f
                );
            
            _animator.SetFloat(MoveRight, _rawMoveInput.x);
            _animator.SetFloat(MoveForward, _rawMoveInput.y);
            _animator.SetBool(Crouch, _isCrouching);
            _animator.SetBool(Run, _alwaysRun || _isRunning);
        }
    }
}
