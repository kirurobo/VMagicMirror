﻿using UnityEngine;

namespace Baku.VMagicMirror
{
    /// <summary>
    /// 各コンポーネントの情報を総合して体の傾き具合(の提案値)に変換するすごいやつだよ
    /// </summary>
    public class BodyLeanIntegrator : MonoBehaviour
    {
        [SerializeField] private GamepadBasedBodyLean _gamepadBasedBodyLean = null;
        [SerializeField] private ImageBasedBodyMotion _imageBasedBodyMotion = null;
        [SerializeField] private FaceAttitudeController _faceAttitudeController = null;

        public Quaternion BodyLeanSuggest { get; private set; }
        
        private void Update()
        {
            //NOTE: 「3つとも角度は小さい+X軸回転だけのハズだし適当に全部かけちゃえー！」という大らかなプログラムです
            BodyLeanSuggest =
                _faceAttitudeController.BodyLeanSuggest *
                _imageBasedBodyMotion.BodyLeanSuggest *
                _gamepadBasedBodyLean.BodyLeanSuggest;
        }
    }
}
