﻿using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using System.Threading.Tasks;
using Zenject;

namespace Baku.VMagicMirror
{
    public class WpfStartAndQuit : MonoBehaviour
    {
        private static readonly string ConfigExePath = "ConfigApp\\VMagicMirrorConfig.exe";

        private static string GetWpfPath()
            => Path.Combine(
                Path.GetDirectoryName(Application.dataPath),
                ConfigExePath
                );

        private IMessageSender _sender = null;
        private List<IReleaseBeforeQuit> _releaseItems = new List<IReleaseBeforeQuit>();

        private readonly Atomic<bool> _releaseRunning = new Atomic<bool>();
        private readonly Atomic<bool> _releaseCompleted = new Atomic<bool>();

        [Inject]
        public void Initialize(IMessageSender sender, List<IReleaseBeforeQuit> releaseNeededItems)
        {
            _sender = sender;
            _releaseItems = releaseNeededItems;
        }
        
        private void Start()
        {
            StartCoroutine(ActivateWpf());
            //NOTE: WPF側はProcess.CloseMainWindowを使ってUnityを閉じようとする。
            //かつ、Unity側で単体で閉じる方法も今のところはメインウィンドウ閉じのみ。
            Application.wantsToQuit += OnApplicationWantsToQuit;
        }

        private bool OnApplicationWantsToQuit()
        {
            if (_releaseCompleted.Value)
            {
                return true;
            }

            if (_releaseRunning.Value)
            {
                return false;
            }
            _releaseRunning.Value = true;
            
            _sender?.SendCommand(MessageFactory.Instance.CloseConfigWindow());

            //特にリリースするものがないケース: 本来ありえないんだけど、理屈上はほしいので書いておく
            if (_releaseItems.Count == 0)
            {
                _releaseCompleted.Value = true;
                _releaseRunning.Value = false;
                return true;
            }
            
            ReleaseItemsAsync();
            return false;
        }

        private async void ReleaseItemsAsync()
        {
            await Task.WhenAll(
                _releaseItems.Select(item => item.ReleaseResources())
            );

            _releaseCompleted.Value = true;
            _releaseRunning.Value = false;
        }

        private IEnumerator ActivateWpf()
        {
            string path = GetWpfPath();
            if (File.Exists(path))
            {
                //他スクリプトの初期化とUpdateが回ってからの方がよいので少しだけ遅らせる
                yield return null;
                yield return null;
#if !UNITY_EDITOR
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                {
                    FileName = path,
                });
#endif
            }
        }
    }
}
