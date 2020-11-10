using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UniRx;
using Zenject;

namespace Baku.VMagicMirror
{
    using static NativeMethods;

    public class WindowStyleController : MonoBehaviour
    {
        static class TransparencyLevel
        {
            public const int None = 0;
            public const int WhenDragDisabledAndOnCharacter = 1;
            public const int WhenOnCharacter = 2;
            public const int WhenDragDisabled = 3;
            public const int Always = 4;
        }

        //Player Settingで決められるデフォルトウィンドウサイズと合わせてるが、常識的な値であれば多少ズレても害はないです
        private const int DefaultWindowWidth = 800;
        private const int DefaultWindowHeight = 600;

        [SerializeField] private float opaqueThreshold = 0.1f;
        [SerializeField] private float windowPositionCheckInterval = 5.0f;
        [SerializeField] private Camera cam = null;
        [SerializeField] private CameraController cameraController = null;

        private float _windowPositionCheckCount = 0;
        private Vector2Int _prevWindowPosition = Vector2Int.zero;

        private uint defaultWindowStyle = 0;
        private uint defaultExWindowStyle = 0;

        private bool _isTransparent = false;
        private bool _isWindowFrameHidden = false;
        private bool _windowDraggableWhenFrameHidden = true;
        private bool _preferIgnoreMouseInput = false;

        private int _hitTestJudgeCountDown = 0;
        //private bool _isMouseLeftButtonDownPreviousFrame = false;
        private bool _isDragging = false;
        private Vector2Int _dragStartMouseOffset = Vector2Int.zero;

        private bool _prevMousePositionInitialized = false;
        private Vector2 _prevMousePosition = Vector2.zero;

        private Renderer[] _renderers = new Renderer[0];

        private Texture2D _colorPickerTexture = null;
        private bool _isOnOpaquePixel = false;
        private bool _isClickThrough = false;
        //既定値がtrueになる(デフォルトでは常時最前面である)ことに注意
        private bool _isTopMost = true;

        int _wholeWindowTransparencyLevel = TransparencyLevel.WhenOnCharacter;
        byte _wholeWindowAlphaWhenTransparent = 0x80;
        //ふつうに起動したら不透明ウィンドウ
        byte _currentWindowAlpha = 0xFF;
        const float AlphaLerpFactor = 0.2f;

        private IDisposable _mouseObserve;

        private readonly WindowAreaIo _windowAreaIo = new WindowAreaIo();

        [Inject]
        public void Initialize(IVRMLoadable vrmLoadable, IMessageReceiver receiver, IKeyMouseEventSource keyboardEventSource)
        {
            receiver.AssignCommandHandler(
                VmmCommands.Chromakey,
                message =>
                {
                    var argb = message.ToColorFloats();
                    SetWindowTransparency(argb[0] == 0);
                });
            receiver.AssignCommandHandler(
                VmmCommands.WindowFrameVisibility,
                message => SetWindowFrameVisibility(message.ToBoolean())
                );
            receiver.AssignCommandHandler(
                VmmCommands.IgnoreMouse,
                message => SetIgnoreMouseInput(message.ToBoolean())
                );
            receiver.AssignCommandHandler(
                VmmCommands.TopMost,
                message => SetTopMost(message.ToBoolean())
                );
            receiver.AssignCommandHandler(
                VmmCommands.WindowDraggable,
                message => SetWindowDraggable(message.ToBoolean())
                );
            receiver.AssignCommandHandler(
                VmmCommands.MoveWindow,
                message =>
                {
                    int[] xy = message.ToIntArray();
                    MoveWindow(xy[0], xy[1]);
                });
            receiver.AssignCommandHandler(
                VmmCommands.ResetWindowSize,
                _ => ResetWindowSize()
                );
            receiver.AssignCommandHandler(
                VmmCommands.SetWholeWindowTransparencyLevel,
                message => SetTransparencyLevel(message.ToInt())
                );
            receiver.AssignCommandHandler(
                VmmCommands.SetAlphaValueOnTransparent,
                message => SetAlphaOnTransparent(message.ToInt())
                );

            _mouseObserve = keyboardEventSource.MouseButton.Subscribe(info =>
            {
                if (info == "LDown")
                {
                    ReserveHitTestJudgeOnNextFrame();
                }
            });
            
            vrmLoadable.PreVrmLoaded += info => _renderers = info.renderers;
            vrmLoadable.VrmDisposing += () => _renderers = new Renderer[0];
        }

        private void Awake()
        {
            IntPtr hWnd = GetUnityWindowHandle();
#if !UNITY_EDITOR
            defaultWindowStyle = GetWindowLong(hWnd, GWL_STYLE);
            defaultExWindowStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            //半透明許可のため、デフォルトで常時レイヤードウィンドウにしておく
            defaultExWindowStyle |= WS_EX_LAYERED;
            SetWindowLong(hWnd, GWL_EXSTYLE, defaultExWindowStyle);
#endif
            CheckSettingFileDirect();
            _windowAreaIo.Load();
            _windowPositionCheckCount = windowPositionCheckInterval;            
        }

        private void Start()
        {
            _colorPickerTexture = new Texture2D(1, 1, TextureFormat.ARGB32, false);

            SetTopMost(_isTopMost);
            StartCoroutine(PickColorCoroutine());
        }

        private void Update()
        {
            UpdateClickThrough();
            UpdateDragStatus();
            UpdateWindowPositionCheck();
            UpdateWindowTransparency();
        }
        
        private void OnDestroy()
        {
            _windowAreaIo.Save();
        }

        private void CheckSettingFileDirect()
        {
            var reader = new DirectSettingFileReader();
            reader.Load();
            if (reader.TransparentBackground)
            {
                //NOTE: このif文の中身には、WPF側で「背景を透過」にチェックを入れた時の挙動の一部を入れているが、
                //見た目に関するものだけにしている(全部やるとクリックスルー設定が絡んで難しくなるので)
                SetWindowTransparency(true);
                SetWindowFrameVisibility(false);
                cameraController.SetCameraBackgroundColor(0, 0, 0, 0);
            }
        }

        private void UpdateClickThrough()
        {
            if (!_isTransparent)
            {
                //不透明ウィンドウ = 絶対にクリックは取る(不透明なのに裏ウィンドウが触れると不自然！)
                SetClickThrough(false);
                return;
            }

            if (!_windowDraggableWhenFrameHidden && _preferIgnoreMouseInput)
            {
                //透明であり、明示的にクリック無視が指定されている = 指定通りにクリックを無視
                SetClickThrough(true);
                return;
            }

            //透明であり、クリックはとってほしい = マウス直下のピクセル状態で判断
            SetClickThrough(!_isOnOpaquePixel);
        }

        private void UpdateDragStatus()
        {
            if (_isWindowFrameHidden &&
                _windowDraggableWhenFrameHidden &&
                _hitTestJudgeCountDown == 1 &&
                _isOnOpaquePixel
                )
            {
                _hitTestJudgeCountDown = 0;
                if (!Application.isFocused)
                {
                    SetUnityWindowActive();
                }
                _isDragging = true;
#if !UNITY_EDITOR
                var mousePosition = GetWindowsMousePosition();
                var windowPosition = GetUnityWindowPosition();
                //以降、このオフセットを保てるようにウィンドウを動かす
                _dragStartMouseOffset = mousePosition - windowPosition;
#endif
            }

            //タッチスクリーンでパッと見の操作が破綻しないために…。
            if (!Input.GetMouseButton(0))
            {
                _isDragging = false;
            }

            if (_isDragging)
            {
#if !UNITY_EDITOR
                var mousePosition = GetWindowsMousePosition();
                SetUnityWindowPosition(
                    mousePosition.x - _dragStartMouseOffset.x, 
                    mousePosition.y - _dragStartMouseOffset.y
                    );
#endif
            }

            if (_hitTestJudgeCountDown > 0)
            {
                _hitTestJudgeCountDown--;
            }
        }

        private void UpdateWindowPositionCheck()
        {
            _windowPositionCheckCount -= Time.deltaTime;
            if (_windowPositionCheckCount > 0)
            {
                return;
            }
            _windowPositionCheckCount = windowPositionCheckInterval;
            
            _windowAreaIo.Check();
        }

        private void UpdateWindowTransparency()
        {
            byte alpha = 0xFF;
            switch (_wholeWindowTransparencyLevel)
            {
                case TransparencyLevel.None:
                    //何もしない: 0xFFで不透明になればOK
                    break;
                case TransparencyLevel.WhenDragDisabledAndOnCharacter:
                    if (!_windowDraggableWhenFrameHidden &&
                        _isOnOpaquePixel)
                    {
                        alpha = _wholeWindowAlphaWhenTransparent;
                    }
                    break;
                case TransparencyLevel.WhenOnCharacter:
                    if (_isOnOpaquePixel)
                    {
                        alpha = _wholeWindowAlphaWhenTransparent;
                    }
                    break;
                case TransparencyLevel.WhenDragDisabled:
                    if (!_windowDraggableWhenFrameHidden)
                    {
                        alpha = _wholeWindowAlphaWhenTransparent;
                    }
                    break;
                case TransparencyLevel.Always:
                    alpha = _wholeWindowAlphaWhenTransparent;
                    break;
                default:
                    break;
            }

            //ウィンドウが矩形なままになっているときは透けさせない
            //(ウィンドウが矩形なときはクリックスルーも認めてないので、透けさせないほうが一貫性が出る)
            if (!_isWindowFrameHidden)
            {
                alpha = 0xFF;
            }

            SetAlpha(alpha);
        }


        private void MoveWindow(int x, int y)
        {
#if !UNITY_EDITOR
            SetUnityWindowPosition(x, y);
#endif
        }

        private void ResetWindowSize()
        {
#if !UNITY_EDITOR
            SetUnityWindowSize(DefaultWindowWidth, DefaultWindowHeight);
#endif
        }

        private void SetWindowFrameVisibility(bool isVisible)
        {
            _isWindowFrameHidden = !isVisible;
            var hwnd = GetUnityWindowHandle();
            uint windowStyle = isVisible ? defaultWindowStyle : WS_POPUP | WS_VISIBLE;
#if !UNITY_EDITOR
            SetWindowLong(hwnd, GWL_STYLE, windowStyle);
            SetUnityWindowTopMost(_isTopMost && _isWindowFrameHidden);
#endif
        }

        private void SetWindowTransparency(bool isTransparent)
        {
            _isTransparent = isTransparent;
#if !UNITY_EDITOR
            SetDwmTransparent(isTransparent);
            ForceWindowResizeEvent();
#endif
        }
        
        private void ForceWindowResizeEvent()
        {
            //NOTE: リサイズ処理は「ズラしてから元に戻す」方式で呼ぶと透過→不透過の切り替え時に画像が歪むのを防げるため、
            //わざと2回呼ぶようにしてます
            if (!GetWindowRect(GetUnityWindowHandle(), out var rect))
            {
                return;
            }

            int cx = rect.right - rect.left;
            int cy = rect.bottom - rect.top;
            
            RefreshWindowSize(cx - 1, cy - 1);
            RefreshWindowSize(cx, cy);
        }
        
        private void SetIgnoreMouseInput(bool ignoreMouseInput)
        {
            _preferIgnoreMouseInput = ignoreMouseInput;
        }

        private void SetTopMost(bool isTopMost)
        {
            _isTopMost = isTopMost;
            //NOTE: 背景透過をしてない = 普通のウィンドウが出てる間は別にTopMostじゃなくていいのがポイント
#if !UNITY_EDITOR
            SetUnityWindowTopMost(_isTopMost && _isWindowFrameHidden);
#endif
        }

        private void SetWindowDraggable(bool isDraggable)
        {
            if (!isDraggable)
            {
                _isDragging = false;
            }

            _windowDraggableWhenFrameHidden = isDraggable;
        }

        private void SetAlphaOnTransparent(int alpha)
        {
            _wholeWindowAlphaWhenTransparent = (byte)alpha;
        }

        private void SetTransparencyLevel(int level)
        {
            _wholeWindowTransparencyLevel = level;
        }

        private void ReserveHitTestJudgeOnNextFrame()
        {
            //UniRxのほうが実行が先なはずなので、
            //1カウント分はメッセージが来た時点のフレームで消費し、
            //さらに1フレーム待ってからヒットテスト判定
            _hitTestJudgeCountDown = 2;

            //タッチスクリーンだとMouseButtonUpが取れない事があるらしいため、この段階でフラグを折る
             _isDragging = false;
        }

        /// <summary>
        /// 背景を透過すべきかの判別方法: キルロボさんのブログ https://qiita.com/kirurobo/items/013cee3fa47a5332e186
        /// </summary>
        /// <returns></returns>
        private IEnumerator PickColorCoroutine()
        {
            var wait = new WaitForEndOfFrame();
            while (Application.isPlaying)
            {
                yield return wait;
                ObservePixelUnderCursor(cam);
            }
            yield return null;
        }

        /// <summary>
        /// マウス直下の画素が透明かどうかを判定
        /// </summary>
        /// <param name="cam"></param>
        private void ObservePixelUnderCursor(Camera cam)
        {
            if (!_prevMousePositionInitialized)
            {
                _prevMousePositionInitialized = true;
                _prevMousePosition = Input.mousePosition;
            }

            Vector2 mousePos = Input.mousePosition;
            //mouse does not move => not need to udpate opacity information
            if ((mousePos - _prevMousePosition).sqrMagnitude < Mathf.Epsilon)
            {
                return;
            }
            _prevMousePosition = mousePos;

            //書いてる通りマウスがウィンドウ外にあるか、またはウィンドウ内であっても明らかに
            //キャラクター上にマウスがないと判断出来る場合は続きを処理しない。
            if (!cam.pixelRect.Contains(mousePos) ||
                !CheckMouseMightBeOnCharacter(mousePos))
            {
                _isOnOpaquePixel = false;
                return;
            }

            try
            {
                // マウス直下の画素を読む (参考 http://tsubakit1.hateblo.jp/entry/20131203/1386000440 )
                _colorPickerTexture.ReadPixels(new Rect(mousePos, Vector2.one), 0, 0);
                Color color = _colorPickerTexture.GetPixel(0, 0);

                // アルファ値がしきい値以上ならば不透過
                _isOnOpaquePixel = (color.a >= opaqueThreshold);
            }
#if UNITY_EDITOR
            catch (Exception ex)
            {
                // 稀に範囲外になるとのこと(元ブログに記載あり)
                Debug.LogError(ex.Message);
#else
            catch (Exception)
            {
                // こっちのケースではex使う用事がないので、コンパイラのご機嫌為にこう書いてます
#endif
                _isOnOpaquePixel = false;
            }
        }

        private bool CheckMouseMightBeOnCharacter(Vector2 mousePosition)
        {
            var ray = cam.ScreenPointToRay(mousePosition);
            //個別のメッシュでバウンディングボックスを調べることで大まかな判定になる仕組み。
            for (int i = 0; i < _renderers.Length; i++)
            {
                if (_renderers[i].bounds.IntersectRay(ray))
                {
                    return true;
                }
            }
            return false;
        }

        private void SetClickThrough(bool through)
        {
            if (_isClickThrough == through)
            {
                return;
            }

            _isClickThrough = through;

            var hwnd = GetUnityWindowHandle();
            uint exWindowStyle = _isClickThrough ?
                WS_EX_LAYERED | WS_EX_TRANSPARENT :
                defaultExWindowStyle;

#if !UNITY_EDITOR
            SetWindowLong(hwnd, GWL_EXSTYLE, exWindowStyle);
#endif
        }

        private void SetAlpha(byte alpha)
        {
            if (alpha == _currentWindowAlpha)
            {
                return;
            }

            byte newAlpha = (byte)Mathf.Clamp(
                Mathf.Lerp(_currentWindowAlpha, alpha, AlphaLerpFactor), 0, 255
                );
            //バイト値刻みで値が変わらないときは、最低でも1ずつ目標値のほうにずらしていく
            if (newAlpha == _currentWindowAlpha &&
                newAlpha != alpha)
            {
                newAlpha += (byte)Mathf.Sign(alpha - newAlpha);
            }

            _currentWindowAlpha = newAlpha;
            SetWindowAlpha(newAlpha);
        }
    }

    /// <summary>
    /// ウィンドウエリアの処理のうち、PlayerPrefsへのセーブ/ロードを含むような所だけ切り出したやつ
    /// </summary>
   class WindowAreaIo
    {
        //前回ソフトが終了したときのウィンドウの位置、およびサイズ
        private const string InitialPositionXKey = "InitialPositionX";
        private const string InitialPositionYKey = "InitialPositionY";
        private const string InitialWidthKey = "InitialWidth";
        private const string InitialHeightKey = "InitialHeight";

        private Vector2Int _prevWindowPosition = Vector2Int.zero;
        
        /// <summary>
        /// アプリ起動時に呼び出すことで、前回に保存した設定があればそれを読みこんで適用します。
        /// また、適用結果によってウィンドウがユーザーから見えない位置に移動した場合は復帰処理を行います。
        /// </summary>
        public void Load()
        {
            if (PlayerPrefs.HasKey(InitialPositionXKey) && PlayerPrefs.HasKey(InitialPositionYKey))
            {
#if !UNITY_EDITOR
                int x = PlayerPrefs.GetInt(InitialPositionXKey);
                int y = PlayerPrefs.GetInt(InitialPositionYKey);
                _prevWindowPosition = new Vector2Int(x, y);
                SetUnityWindowPosition(x, y);
#endif
            }
            else
            {
#if !UNITY_EDITOR
                _prevWindowPosition = GetUnityWindowPosition();
                PlayerPrefs.SetInt(InitialPositionXKey, _prevWindowPosition.x);
                PlayerPrefs.SetInt(InitialPositionYKey, _prevWindowPosition.y);
#endif
            }

            int width = PlayerPrefs.GetInt(InitialWidthKey, 0);
            int height = PlayerPrefs.GetInt(InitialHeightKey, 0);
            if (width > 100 && height > 100)
            {
#if !UNITY_EDITOR
                SetUnityWindowSize(width, height);
#endif
            }
            
            AdjustIfWindowPositionInvalid();
        }
        
        /// <summary> アプリ起動中に呼び出すことで、ウィンドウが移動していればその位置を記録します。 </summary>
        public void Check() 
        {
#if !UNITY_EDITOR
            var pos = GetUnityWindowPosition();
            if (pos.x != _prevWindowPosition.x ||
                pos.y != _prevWindowPosition.y)
            {
                _prevWindowPosition = pos;
                PlayerPrefs.SetInt(InitialPositionXKey, _prevWindowPosition.x);
                PlayerPrefs.SetInt(InitialPositionYKey, _prevWindowPosition.y);
            }
#endif
        }
        
        /// <summary> アプリ終了時に呼び出すことで、終了時のウィンドウ位置、およびサイズを記録します。 </summary>
        public void Save()
        {
            if (GetWindowRect(GetUnityWindowHandle(), out var rect))
            {
#if !UNITY_EDITOR
                PlayerPrefs.SetInt(InitialPositionXKey, rect.left);
                PlayerPrefs.SetInt(InitialPositionYKey, rect.top);
                PlayerPrefs.SetInt(InitialWidthKey, rect.right - rect.left);
                PlayerPrefs.SetInt(InitialHeightKey, rect.bottom - rect.top);
#endif
            }
        }

        private void AdjustIfWindowPositionInvalid()
        {
            if (!GetWindowRect(GetUnityWindowHandle(), out var rect))
            {
                LogOutput.Instance.Write("Failed to get self window rect, could not start window position adjust");
                return;
            }
                
            var monitorRects = LoadAllMonitorRects();
            if (!CheckWindowPositionValidity(rect, monitorRects))
            {
                MoveToPrimaryMonitorBottomRight(rect);
            }         
        }

        private bool CheckWindowPositionValidity(RECT selfRect, List<RECT> monitorRects)
        {
            //条件: どれか一つのモニター領域について以下を満たしていれば、想定どおりその位置にウィンドウを置いている、と推定する
            // - 自身のウィンドウの左上隅がそのモニターの内側である
            // - ウィンドウのタテヨコともに2/3以上がそのモニターに収まっている
            foreach (var r in monitorRects)
            {
                if (!(selfRect.left >= r.left && selfRect.left < r.right && 
                      selfRect.top >= r.top && selfRect.top < r.bottom))
                {
                    //左上隅がこのモニターに収まってない→関係ないウィンドウなので無視
                    continue;
                }
                
                //左上隅が収まってるモニターだった: ウィンドウの右下2/3のポイントがモニター内にあるかチェックし、
                //極端にウィンドウが見切れてないか確認
                int testX = selfRect.left + (selfRect.right - selfRect.left) * 2 / 3; 
                int testY = selfRect.top + (selfRect.bottom - selfRect.top) * 2 / 3;

                //NOTE: ほぼ起きないハズだけど、同じ座標が2つのモニターに含まれていると判定されるケースに備え、
                //ここの判定がfalseだった場合も他のモニターをチェックしていいような書き方にしておく
                if (testX >= r.left && testX < r.right && testY >= r.top && testY < r.bottom)
                {
                    return true;
                }
            }
            return false;
        }

        //プライマリモニターの右下にウィンドウを移動したのち、ウィンドウサイズが大きすぎない事を保証し、
        //その状態でウィンドウの位置/サイズを保存します。
        //この処理は異常復帰なので、ちょっと余裕をもってウィンドウを動かすことに注意して下さい。
        private void MoveToPrimaryMonitorBottomRight(RECT selfRect)
        {
            var primaryWindowRect = GetPrimaryWindowRect();

            //ウィンドウを画面の4隅から確実に離せるようにサイズの上限 + 位置の調整を行う。
            //画面の4隅どこにタスクバーがあってもなるべく避けるとか、
            //透過ウィンドウを非透過にしたらタイトルバーが画面外に行っちゃった、というケースを避けるのが狙い
            int width = Mathf.Min(
                selfRect.right - selfRect.left,
                primaryWindowRect.right - primaryWindowRect.left - 200
                );
            int height = Mathf.Min(
                selfRect.bottom - selfRect.top,
                primaryWindowRect.bottom - primaryWindowRect.top - 200
                );
            
            int x = Mathf.Clamp(
                selfRect.left, primaryWindowRect.left + 100, primaryWindowRect.right - 100 - width
                );
            int y = Mathf.Clamp(
                selfRect.top, primaryWindowRect.top + 100, primaryWindowRect.bottom - 100 - height
                );

#if !UNITY_EDITOR
            _prevWindowPosition = new Vector2Int(x, y);
            PlayerPrefs.SetInt(InitialPositionXKey, x);
            PlayerPrefs.SetInt(InitialPositionYKey, y);
            PlayerPrefs.SetInt(InitialWidthKey, width);
            PlayerPrefs.SetInt(InitialHeightKey, height);

            SetUnityWindowPosition(x, y);
            SetUnityWindowSize(width, height);
#endif
        }
    }
}