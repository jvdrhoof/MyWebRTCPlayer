using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
// using System.Net.WebSockets;
using UnityEngine;
using UnityEngine.UI;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace Unity.WebRTC.Samples
{
    class Caller : MonoBehaviour
    {
#pragma warning disable 0649
        [SerializeField] private Button hangUpButton;
        [SerializeField] private Dropdown webCamListDropdown;
        [SerializeField] private Dropdown micListDropdown;
        [SerializeField] private Camera cam;
        [SerializeField] private AudioClip clip;
        [SerializeField] private RawImage sourceImage;
        [SerializeField] private AudioSource sourceAudio;
        [SerializeField] private RawImage receiveImage;
        [SerializeField] private AudioSource receiveAudio;
        [SerializeField] private Transform rotateObject;
#pragma warning restore 0649

        private RTCPeerConnection peerConnection;
        private List<RTCRtpSender> peerConnectionSenders;
        private VideoStreamTrack videoStreamTrack;
        private AudioStreamTrack audioStreamTrack;
        private MediaStream receiveAudioStream, receiveVideoStream;
        private DelegateOnIceConnectionChange onIceConnectionChange;
        private DelegateOnIceCandidate onIceCandidate;
        private DelegateOnTrack onTrack;
        private WebCamTexture webCamTexture;

        private WebSocket ws;
        private string localDescription;
        private string localCandidate;
        private string remoteDescription;
        private string remoteCandidate;
        private int phase = 0;

        private void Awake()
        {
            hangUpButton.onClick.AddListener(HangUp);
            webCamListDropdown.options = WebCamTexture.devices.Select(x => new Dropdown.OptionData(x.name)).ToList();
            micListDropdown.options = Microphone.devices.Select(x => new Dropdown.OptionData(x)).ToList();
        }

        private void OnDestroy()
        {
            if (webCamTexture != null)
            {
                webCamTexture.Stop();
                webCamTexture = null;
            }
        }

        private void Start()
        {
            Debug.Log("Starting peer");

            ws = new WebSocket("ws://localhost:8000/WebRTC");

            ws.OnMessage += (sender, e) =>
            {
                Debug.Log("Message Received from " + ((WebSocket)sender).Url + ", Data : " + e.Data);
                if (e.Data[0] == 'd')
                {
                    phase = 1;
                    remoteDescription = e.Data[1..];
                    Debug.Log("Received Description:");
                    Debug.Log(remoteDescription);
                }
                else if (e.Data[0] == 'c')
                {
                    phase = 3;
                    remoteCandidate = e.Data[1..];
                    Debug.Log("Received Candidate");
                    Debug.Log(remoteCandidate);
                }
            };

            ws.OnOpen += (sender, e) =>
            {
                Debug.Log("Ws opened");
                ws.Send("oHello");
            };

            ws.Connect();

            peerConnectionSenders = new List<RTCRtpSender>();

            onIceConnectionChange = state => { OnIceConnectionChange(peerConnection, state); };
            onIceCandidate = candidate => { OnIceCandidate(peerConnection, candidate); };
            onTrack = e =>
            {
                if (e.Track is VideoStreamTrack video)
                {
                    video.OnVideoReceived += tex =>
                    {
                        receiveImage.texture = tex;
                    };
                }

                if (e.Track is AudioStreamTrack audioTrack)
                {
                    receiveAudio.SetTrack(audioTrack);
                    receiveAudio.loop = true;
                    receiveAudio.Play();
                }
            };

            // StartCoroutine(WebRTC.Update());
        }

        private void Update()
        {
            if (phase == 1)
            {
                StartCoroutine(Call());
            }
            else if (phase == 3)
            {
                RTCIceCandidateInit can = new RTCIceCandidateInit();
                can.candidate = remoteCandidate;
                can.sdpMid = "0";
                peerConnection.AddIceCandidate(new RTCIceCandidate(can));
                ws.Send('c' + localCandidate);
                phase = 4;
                Debug.Log("Sending local candidate to server");
            }
        }

        private static RTCConfiguration GetSelectedSdpSemantics()
        {
            RTCConfiguration config = default;
            return config;
        }

        private void OnIceConnectionChange(RTCPeerConnection pc, RTCIceConnectionState state)
        {
            switch (state)
            {
                case RTCIceConnectionState.New:
                    Debug.Log("IceConnectionState: New");
                    break;
                case RTCIceConnectionState.Checking:
                    Debug.Log("IceConnectionState: Checking");
                    break;
                case RTCIceConnectionState.Closed:
                    Debug.Log("IceConnectionState: Closed");
                    break;
                case RTCIceConnectionState.Completed:
                    Debug.Log("IceConnectionState: Completed");
                    break;
                case RTCIceConnectionState.Connected:
                    Debug.Log("IceConnectionState: Connected");
                    break;
                case RTCIceConnectionState.Disconnected:
                    Debug.Log("IceConnectionState: Disconnected");
                    break;
                case RTCIceConnectionState.Failed:
                    Debug.Log("IceConnectionState: Failed");
                    break;
                case RTCIceConnectionState.Max:
                    Debug.Log("IceConnectionState: Max");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }
        }

        private IEnumerator Call()
        {
            hangUpButton.interactable = true;

            phase = 2;
            Debug.Log("GetSelectedSdpSemantics");
            var configuration = GetSelectedSdpSemantics();
            peerConnection = new RTCPeerConnection(ref configuration);

            peerConnection.OnIceCandidate = onIceCandidate;
            peerConnection.OnIceConnectionChange = onIceConnectionChange;

            RTCSessionDescription desc;
            desc.sdp = remoteDescription;
            desc.type = RTCSdpType.Offer;
            peerConnection.SetRemoteDescription(ref desc);
            RTCDataChannelInit conf = new RTCDataChannelInit();
            var op = peerConnection.CreateAnswer();
            yield return op;
            if (!op.IsError)
            {
                Debug.Log(op.Desc.sdp);
                yield return (StartCoroutine(OnCreateAnswerSuccess(op.Desc)));
            }
            else
            {
                OnCreateSessionDescriptionError(op.Error);
            }

            CaptureAudioStart();
            StartCoroutine(CaptureVideoStart());

            var videoSender = peerConnection.AddTrack(videoStreamTrack);
            peerConnectionSenders.Add(videoSender);
            peerConnectionSenders.Add(peerConnection.AddTrack(audioStreamTrack));

            if (WebRTCSettings.UseVideoCodec != null)
            {
                var codecs = new[] { WebRTCSettings.UseVideoCodec };
                var transceiver = peerConnection.GetTransceivers().First(t => t.Sender == videoSender);
                transceiver.SetCodecPreferences(codecs);
            }
        }

        private void CaptureAudioStart()
        {
            var deviceName = Microphone.devices[micListDropdown.value];
            Microphone.GetDeviceCaps(deviceName, out int minFreq, out int maxFreq);
            var micClip = Microphone.Start(deviceName, true, 1, 48000);

            // set the latency to “0” samples before the audio starts to play.
            while (!(Microphone.GetPosition(deviceName) > 0)) { }

            sourceAudio.clip = micClip;
            sourceAudio.loop = true;
            sourceAudio.Play();
            audioStreamTrack = new AudioStreamTrack(sourceAudio);
        }

        private IEnumerator CaptureVideoStart()
        {
            if (WebCamTexture.devices.Length == 0)
            {
                Debug.LogFormat("WebCam device not found");
                yield break;
            }

            yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
            if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
            {
                Debug.LogFormat("authorization for using the device is denied");
                yield break;
            }

            WebCamDevice userCameraDevice = WebCamTexture.devices[webCamListDropdown.value];
            webCamTexture = new WebCamTexture(userCameraDevice.name, WebRTCSettings.StreamSize.x, WebRTCSettings.StreamSize.y, 30);
            webCamTexture.Play();
            yield return new WaitUntil(() => webCamTexture.didUpdateThisFrame);

            videoStreamTrack = new VideoStreamTrack(webCamTexture);
            sourceImage.texture = webCamTexture;
        }

        private void HangUp()
        {
            if (webCamTexture != null)
            {
                webCamTexture.Stop();
                webCamTexture = null;
            }

            receiveAudioStream?.Dispose();
            receiveAudioStream = null;
            receiveVideoStream?.Dispose();
            receiveVideoStream = null;

            videoStreamTrack?.Dispose();
            videoStreamTrack = null;
            audioStreamTrack?.Dispose();
            audioStreamTrack = null;

            Debug.Log("Close local/remote peer connection");
            peerConnection?.Dispose();
            peerConnection = null;
            sourceImage.texture = null;
            sourceAudio.Stop();
            sourceAudio.clip = null;
            receiveImage.texture = null;
            receiveAudio.Stop();
            receiveAudio.clip = null;
            hangUpButton.interactable = false;
        }

        private void OnIceCandidate(RTCPeerConnection pc, RTCIceCandidate candidate)
        {
            Debug.Log(candidate.Address);
            Debug.Log(candidate.Candidate);
            localCandidate = candidate.Candidate;
        }

        private void OnSetLocalSuccess(RTCPeerConnection pc)
        {
            ws.Send("d" + localDescription);
            Debug.Log("SetLocalDescription complete");
        }

        static void OnSetSessionDescriptionError(ref RTCError error)
        {
            Debug.LogError($"Error Detail Type: {error.message}");
        }

        IEnumerator OnCreateAnswerSuccess(RTCSessionDescription desc)
        {
            Debug.Log($"Answer:\n{desc.sdp}");
            Debug.Log("SetLocalDescription start");

            localDescription = desc.sdp;
            var op = peerConnection.SetLocalDescription(ref desc);
            yield return op;

            if (!op.IsError)
            {
                OnSetLocalSuccess(peerConnection);
            }
            else
            {
                var error = op.Error;
                OnSetSessionDescriptionError(ref error);
            }
        }

        private static void OnCreateSessionDescriptionError(RTCError error)
        {
            Debug.LogError($"Error Detail Type: {error.message}");
        }
    }
}
