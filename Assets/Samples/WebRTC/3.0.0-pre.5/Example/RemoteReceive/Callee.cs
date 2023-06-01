using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
// using System.Net.WebSockets;
using UnityEngine;
using UnityEngine.UI;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace Unity.WebRTC.Samples
{
    public class Callee : MonoBehaviour
    {
#pragma warning disable 0649
        [SerializeField] public Button hangUpButton;
        [SerializeField] public Dropdown webCamListDropdown;
        [SerializeField] public Dropdown micListDropdown;
        [SerializeField] public Camera cam;
        [SerializeField] public AudioClip clip;
        [SerializeField] public RawImage sourceImage;
        [SerializeField] public AudioSource sourceAudio;
        [SerializeField] public RawImage receiveImage;
        [SerializeField] public AudioSource receiveAudio;
        [SerializeField] public Transform rotateObject;
#pragma warning restore 0649

        public List<string> candidates = new List<string>();

        public RTCPeerConnection peerConnection;
        public List<RTCRtpSender> peerConnectionSenders;
        public VideoStreamTrack videoStreamTrack;
        public AudioStreamTrack audioStreamTrack;
        public MediaStream receiveAudioStream, receiveVideoStream;
        public DelegateOnIceConnectionChange onIceConnectionChange;
        public DelegateOnIceCandidate onIceCandidate;
        public DelegateOnTrack onTrack;
        public WebCamTexture webCamTexture;

        public WebSocket ws;
        public string localDescription;
        public string remoteDescription;

        public int phase = 0;
        public int spdMid = 0;

        public void Awake()
        {
            hangUpButton.onClick.AddListener(HangUp);
            webCamListDropdown.options = WebCamTexture.devices.Select(x => new Dropdown.OptionData(x.name)).ToList();
            micListDropdown.options = Microphone.devices.Select(x => new Dropdown.OptionData(x)).ToList();
        }

        public void OnDestroy()
        {
            if (webCamTexture != null)
            {
                webCamTexture.Stop();
                webCamTexture = null;
            }
        }

        public void OnSetRemoteSuccess()
        {
            Debug.Log("Remote description set successfully");
        }

        public IEnumerator PeerNegotiationNeeded()
        {
            Debug.Log("Peer negotiation started");
            var op = peerConnection.CreateOffer();
            yield return op;
            if (!op.IsError)
            {
                if (peerConnection.SignalingState != RTCSignalingState.Stable)
                {
                    Debug.LogError("Signaling state is not stable.");
                    yield break;
                }
                yield return (StartCoroutine(OnCreateOfferSuccess(op.Desc)));
            }
            else
            {
                OnCreateSessionDescriptionError(op.Error);
            }
        }

        public void OnCreateOffer()
        {
            Debug.Log($"Interest was shown, creating an offer");

            hangUpButton.interactable = true;

            Debug.Log($"Setting up new peer connection");

            var configuration = GetSelectedSdpSemantics();
            peerConnection = new RTCPeerConnection(ref configuration);
            peerConnection.OnIceCandidate = onIceCandidate;
            peerConnection.OnIceConnectionChange = onIceConnectionChange;
            peerConnection.OnTrack = onTrack;
            peerConnection.OnNegotiationNeeded = () => { StartCoroutine(PeerNegotiationNeeded()); };
            StartCoroutine(WebRTC.Update());

            CaptureAudioStart();
            StartCoroutine(CaptureVideoStart());
        }

        public IEnumerator OnReceiveOffer()
        {
            Debug.Log($"Offer received: {remoteDescription}");

            hangUpButton.interactable = true;

            Debug.Log($"Setting up new peer connection");

            var configuration = GetSelectedSdpSemantics();
            peerConnection = new RTCPeerConnection(ref configuration);
            peerConnection.OnIceCandidate = onIceCandidate;
            peerConnection.OnIceConnectionChange = onIceConnectionChange;
            peerConnection.OnTrack = onTrack;
            // StartCoroutine(WebRTC.Update());

            Debug.Log("Setting remote description");

            RTCSessionDescription desc;
            desc.sdp = remoteDescription;
            desc.type = RTCSdpType.Offer;

            var op_1 = peerConnection.SetRemoteDescription(ref desc);
            yield return op_1;
            if (!op_1.IsError)
            {
                OnSetRemoteSuccess();
            }
            else
            {
                var error = op_1.Error;
                OnSetSessionDescriptionError(ref error);
            }

            Debug.Log("Creating answer");

            var op_2 = peerConnection.CreateAnswer();
            yield return op_2;
            if (!op_2.IsError)
            {
                yield return (StartCoroutine(OnCreateAnswerSuccess(op_2.Desc)));
            }
            else
            {
                OnCreateSessionDescriptionError(op_2.Error);
            }

            CaptureAudioStart();
            StartCoroutine(CaptureVideoStart());
        }

        public IEnumerator OnReceiveAnswer()
        {
            Debug.Log($"Answer received: {remoteDescription}");

            Debug.Log("Setting remote description");

            RTCSessionDescription desc;
            desc.sdp = remoteDescription;
            desc.type = RTCSdpType.Answer;

            var op = peerConnection.SetRemoteDescription(ref desc);
            yield return op;
            if (!op.IsError)
            {
                OnSetRemoteSuccess();
            }
            else
            {
                var error = op.Error;
                OnSetSessionDescriptionError(ref error);
            }
        }

        public void Start()
        {
            Debug.Log("Starting websocket");

            ws = new WebSocket("ws://145.90.222.224:8000");

            ws.OnMessage += (sender, e) =>
            {
                if (e.Data[0] == 'o')
                {
                    Debug.Log("Received oHello");
                    phase = 200;
                }
                else if (e.Data[0] == 'd')
                {
                    remoteDescription = e.Data[1..];
                    Debug.Log("Received description");
                    if (phase == 0)
                    {
                        phase = 300;
                    }
                    else
                    {
                        phase = 400;
                    }
                }
                else if (e.Data[0] == 'c')
                {
                    candidates.Add(e.Data[1..]);
                    Debug.Log("Received candidate");
                }
            };

            ws.OnOpen += (sender, e) =>
            {
                Debug.Log("Web socket connection opened");
                ws.Send("oHello");
            };

            ws.Connect();

            peerConnectionSenders = new List<RTCRtpSender>();

            onIceConnectionChange = state => { OnIceConnectionChange(peerConnection, state); };
            onIceCandidate = candidate => { OnIceCandidate(peerConnection, candidate); };
            onTrack = e =>
            {
                Debug.Log("OnTrack triggered!");
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

            StartCoroutine(WebRTC.Update());
        }

        public void Update()
        {
            if (phase == 200)
            {
                phase = 201;
                OnCreateOffer();
            }
            else if (phase == 300)
            {
                phase = 500;
                StartCoroutine(OnReceiveOffer());
            }
            else if (phase == 400)
            {
                phase = 600;
                StartCoroutine(OnReceiveAnswer());
            }

            foreach (string candidate in candidates)
            {
                Debug.Log($"Candidate received: {candidate}");
                RTCIceCandidateInit can = new RTCIceCandidateInit();
                can.candidate = candidate;
                can.sdpMid = $"{spdMid}";
                spdMid += 1;
                peerConnection.AddIceCandidate(new RTCIceCandidate(can));
            }

            candidates = new List<string>();
        }

        public static RTCConfiguration GetSelectedSdpSemantics()
        {
            RTCConfiguration config = default;
            config.iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } };
            return config;
        }

        public void OnIceConnectionChange(RTCPeerConnection peerConnection, RTCIceConnectionState state)
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

        public void CaptureAudioStart()
        {
            /*var deviceName = Microphone.devices[micListDropdown.value];
            Microphone.GetDeviceCaps(deviceName, out int minFreq, out int maxFreq);
            var micClip = Microphone.Start(deviceName, true, 1, 48000);

            // set the latency to “0” samples before the audio starts to play.
            while (!(Microphone.GetPosition(deviceName) > 0)) {}

            sourceAudio.clip = micClip;
            sourceAudio.loop = true;
            sourceAudio.Play();
            audioStreamTrack = new AudioStreamTrack(sourceAudio);*/
        }

        public IEnumerator CaptureVideoStart()
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

            var videoSender = peerConnection.AddTrack(videoStreamTrack);
            peerConnectionSenders.Add(videoSender);
            // peerConnectionSenders.Add(peerConnection.AddTrack(audioStreamTrack));

            if (WebRTCSettings.UseVideoCodec != null)
            {
                var codecs = new[] { WebRTCSettings.UseVideoCodec };
                var transceiver = peerConnection.GetTransceivers().First(t => t.Sender == videoSender);
                transceiver.SetCodecPreferences(codecs);
            }
        }

        public void HangUp()
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

        public void OnIceCandidate(RTCPeerConnection pc, RTCIceCandidate candidate)
        {
            ws.Send('c' + candidate.Candidate);
            Debug.Log($"Local candidate {candidate.Candidate} sent to peer");
        }

        public void OnSetLocalSuccess()
        {
            ws.Send("d" + localDescription);
            Debug.Log("SetLocalDescription complete");
        }

        IEnumerator OnCreateOfferSuccess(RTCSessionDescription desc)
        {
            Debug.Log($"Offer: {desc.sdp}");

            localDescription = desc.sdp;
            var op = peerConnection.SetLocalDescription(ref desc);
            yield return op;
            if (!op.IsError)
            {
                OnSetLocalSuccess();
            }
            else
            {
                var error = op.Error;
                OnSetSessionDescriptionError(ref error);
            }
        }

        IEnumerator OnCreateAnswerSuccess(RTCSessionDescription desc)
        {
            Debug.Log($"Answer: {desc.sdp}");

            localDescription = desc.sdp;
            var op = peerConnection.SetLocalDescription(ref desc);
            yield return op;
            if (!op.IsError)
            {
                OnSetLocalSuccess();
            }
            else
            {
                var error = op.Error;
                OnSetSessionDescriptionError(ref error);
            }
        }

        static void OnSetSessionDescriptionError(ref RTCError error)
        {
            Debug.LogError($"Error Detail Type: {error.message}");
        }

        public static void OnCreateSessionDescriptionError(RTCError error)
        {
            Debug.LogError($"Error Detail Type: {error.message}");
        }
    }
}
