using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using WebSocketSharp;

namespace Unity.WebRTC.Samples
{
    public class RemotePeer : MonoBehaviour
    {
#pragma warning disable 0649
        [SerializeField] private Button callButton;
        [SerializeField] private Button hangUpButton;
        [SerializeField] private Dropdown webCamListDropdown;
        [SerializeField] private Dropdown micListDropdown;
        [SerializeField] public RawImage sourceImage;
        [SerializeField] public AudioSource sourceAudio;
        [SerializeField] public RawImage receiveImage;
        [SerializeField] public AudioSource receiveAudio;
#pragma warning restore 0649

        // Parameters related to video conferencing
        public RTCPeerConnection peerConnection;
        public List<RTCRtpSender> peerConnectionSenders;
        public VideoStreamTrack videoStreamTrack;
        public AudioStreamTrack audioStreamTrack;
        public MediaStream receiveAudioStream, receiveVideoStream;
        private WebCamTexture webCamTexture;

        // Parameters related to WebSockets
        public string webSocketServerAddress;
        public int webSocketServerPort;
        private WebSocket ws;

        // Parameters related to SDP
        private string remoteDescription;
        private List<string> candidates = new();
        private int spdMid;

        // Custom state used in the peer signaling process
        private enum State { Idle = 0, Hello = 1, Offer = 2, Answer = 3, Ready = 4, Finished = 5 };
        private State state;

        public void Awake()
        {
            callButton.onClick.AddListener(Call);
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

        public void Start()
        {
            // Define GUI behavior
            webCamListDropdown.interactable = true;
            micListDropdown.interactable = true;
            callButton.interactable = true;
            hangUpButton.interactable = false;
        }

        public void Call()
        {
            // Define GUI behavior
            webCamListDropdown.interactable = false;
            micListDropdown.interactable = false;
            callButton.interactable = false;
            hangUpButton.interactable = true;

            // Connect to WebSocket server
            ws = new WebSocket($"ws://{webSocketServerAddress}:{webSocketServerPort}/");

            // Handle incoming messages
            ws.OnMessage += (sender, e) =>
            {
                char message_type = e.Data[0];
                string message = e.Data[1..];
                switch (message_type)
                {
                    case 'h':
                        Debug.Log("Received hello");
                        state = State.Hello;
                        break;
                    case 'o':
                        Debug.Log("Received offer");
                        remoteDescription = message;
                        state = State.Offer;
                        break;
                    case 'a':
                        Debug.Log("Received answer");
                        remoteDescription = message;
                        state = State.Answer;
                        break;
                    case 'c':
                        Debug.Log($"Received candidate {message}");
                        RTCIceCandidateInit can = new RTCIceCandidateInit();
                        can.candidate = message;
                        can.sdpMid = $"{spdMid}";
                        spdMid += 1;
                        peerConnection.AddIceCandidate(new RTCIceCandidate(can));
                        break;
                    default:
                        Debug.Log($"Received non-compliant message: {message}");
                        break;
                }
            };

            // Action to take upon establishing connection
            ws.OnOpen += (sender, e) =>
            {
                Debug.Log("WebSocket connection established");
                ws.Send("hello");
            };

            Debug.Log("Establishing WebSocket connection");
            ws.Connect();

            // Start asynchronous WebRTC coroutine
            StartCoroutine(WebRTC.Update());

            // Start selected camera
            WebCamDevice userCameraDevice = WebCamTexture.devices[webCamListDropdown.value];
            webCamTexture = new WebCamTexture(userCameraDevice.name, WebRTCSettings.StreamSize.x, WebRTCSettings.StreamSize.y, 30);
            webCamTexture.Play();
            new WaitUntil(() => webCamTexture.didUpdateThisFrame);
            videoStreamTrack = new VideoStreamTrack(webCamTexture);
            sourceImage.texture = webCamTexture;

            // Start selected microphone
            string deviceName = Microphone.devices[micListDropdown.value];
            Microphone.GetDeviceCaps(deviceName, out int minFreq, out int maxFreq);
            var micClip = Microphone.Start(deviceName, true, 1, 48000);
            while (!(Microphone.GetPosition(deviceName) > 0)) { }
            sourceAudio.clip = micClip;
            sourceAudio.loop = true;
            sourceAudio.Play();
            audioStreamTrack = new AudioStreamTrack(sourceAudio);

            // Start peer connection
            var configuration = GetSelectedSdpSemantics();
            peerConnection = new RTCPeerConnection(ref configuration);
            peerConnection.OnIceConnectionChange = state => { OnIceConnectionChange(state); };
            peerConnection.OnIceCandidate = candidate => { OnIceCandidate(candidate); };
            peerConnection.OnTrack = e => { OnTrack(e); };

            // Add tracks to peer connection
            var videoSender = peerConnection.AddTrack(videoStreamTrack);
            var audioSender = peerConnection.AddTrack(audioStreamTrack);
            peerConnectionSenders = new List<RTCRtpSender> { videoSender, audioSender };

            // Set codec preferences
            if (WebRTCSettings.UseVideoCodec != null)
            {
                var codecs = new[] { WebRTCSettings.UseVideoCodec };
                var transceiver = peerConnection.GetTransceivers().First(t => t.Sender == videoSender);
                transceiver.SetCodecPreferences(codecs);
            }
        }

        private void HangUp()
        {
            // Define GUI behavior
            webCamListDropdown.interactable = true;
            micListDropdown.interactable = true;
            callButton.interactable = true;
            hangUpButton.interactable = false;

            // Stop video stream and audio track
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
            sourceImage.texture = null;
            sourceAudio.Stop();
            sourceAudio.clip = null;
            receiveImage.texture = null;
            receiveAudio.Stop();
            receiveAudio.clip = null;

            // Destroy peer connection
            peerConnection?.Dispose();
            peerConnection = null;

            Debug.Log("Video conferencing session closed");
        }

        public void Update()
        {
            // Start asynchronous coroutines when required
            switch (state)
            {
                case State.Hello:
                    StartCoroutine(OnReceiveHello());
                    state = State.Idle;
                    break;
                case State.Offer:
                    StartCoroutine(OnReceiveOffer());
                    state = State.Idle;
                    break;
                case State.Answer:
                    StartCoroutine(OnReceiveAnswer());
                    state = State.Idle;
                    break;
                case State.Ready:
                    foreach (string candidate in candidates)
                    {
                        ws.Send('c' + candidate);
                        Debug.Log($"Sent local candidate {candidate}");
                    }
                    state = State.Finished;
                    break;
                default:
                    break;
            }
        }

        public static RTCConfiguration GetSelectedSdpSemantics()
        {
            // Use default configurations with a custom STUN server
            RTCConfiguration config = default;
            config.iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } };
            return config;
        }

        public IEnumerator OnReceiveHello()
        {
            Debug.Log("Peer negotiation started");

            // Create SDP offer
            var op_1 = peerConnection.CreateOffer();
            yield return op_1;
            var desc = op_1.Desc;
            if (!op_1.IsError)
            {
                ws.Send("o" + desc.sdp);
                Debug.Log("Offer sent to peer");
            }
            else
            {
                Debug.LogError($"Error Detail Type: {op_1.Error.message}");
            }

            // Set local SDP description
            var op_2 = peerConnection.SetLocalDescription(ref desc);
            yield return op_2;
            if (!op_2.IsError)
            {
                Debug.Log($"Local description set");
            }
            else
            {
                Debug.LogError($"Error Detail Type: {op_2.Error.message}");
            }
        }

        public IEnumerator OnReceiveOffer()
        {
            Debug.Log($"Offer received: {remoteDescription}");

            // Recreate SDP offer from text
            RTCSessionDescription desc;
            desc.sdp = remoteDescription;
            desc.type = RTCSdpType.Offer;

            // Set remote SDP connection
            var op_1 = peerConnection.SetRemoteDescription(ref desc);
            yield return op_1;
            if (!op_1.IsError)
            {
                Debug.Log("Remote description set");
            }
            else
            {
                Debug.LogError($"Error Detail Type: {op_1.Error.message}");
            }

            // Create SDP answer
            var op_2 = peerConnection.CreateAnswer();
            yield return op_2;
            desc = op_2.Desc;
            if (!op_2.IsError)
            {
                ws.Send("a" + desc.sdp);
                Debug.Log("Answer sent to peer");
            }
            else
            {
                Debug.LogError($"Error Detail Type: {op_2.Error.message}");
            }

            // Set local SDP description
            var op_3 = peerConnection.SetLocalDescription(ref desc);
            yield return op_3;
            if (!op_3.IsError)
            {
                state = State.Ready;
                Debug.Log("Local description set");
            }
            else
            {
                Debug.LogError($"Error Detail Type: {op_3.Error.message}");
            }
        }

        public IEnumerator OnReceiveAnswer()
        {
            Debug.Log($"Answer received: {remoteDescription}");

            // Recreate SDP answer from text
            RTCSessionDescription desc;
            desc.sdp = remoteDescription;
            desc.type = RTCSdpType.Answer;

            // Set remote SDP connection
            var op = peerConnection.SetRemoteDescription(ref desc);
            yield return op;
            if (!op.IsError)
            {
                state = State.Ready;
                Debug.Log("Remote description set");
            }
            else
            {
                Debug.LogError($"Error Detail Type: {op.Error.message}");
            }
        }

        public void OnIceConnectionChange(RTCIceConnectionState state)
        {
            // Allow logging of ICE connection changes
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

        public void OnIceCandidate(RTCIceCandidate candidate)
        {
            // As long as the local and the remote description have not yet been set,
            // temporarily store local candidates for future WebSocket signaling
            if (state != State.Finished)
            {
                candidates.Add(candidate.Candidate);
                Debug.Log($"Added local candidate {candidate.Candidate}");
            }
            // Once both parameters have been set, any new candidate can immediately
            // be released to the other peer
            else
            {
                ws.Send('c' + candidate.Candidate);
                Debug.Log($"Sent local candidate {candidate.Candidate}");
            }
        }

        public void OnTrack(RTCTrackEvent e)
        {
            Debug.Log($"OnTrack triggered");
            // Add any incoming video or audio tracks to Unity's texture/track
            if (e.Track is VideoStreamTrack video)
            {
                video.OnVideoReceived += tex =>
                {
                    receiveImage.texture = tex;
                };
            }
            else if (e.Track is AudioStreamTrack audioTrack)
            {
                receiveAudio.SetTrack(audioTrack);
                receiveAudio.loop = true;
                receiveAudio.Play();
            }
        }
    }
}
