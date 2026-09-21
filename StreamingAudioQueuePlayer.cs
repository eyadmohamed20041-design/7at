using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

// =====================================================================
// StreamingAudioQueuePlayer
//
// Receives MP3 sentence chunks (one full, independent MP3 file per
// chunk) as they arrive over the WebSocket, decodes each one into an
// AudioClip, and plays them back in strict sequence order on a single
// AudioSource - starting playback as soon as the first clip is ready,
// without waiting for the rest of the response.
//
// Decoding uses UnityWebRequestMultimedia.GetAudioClip against a local
// temp file, matching the same mechanism the project already uses for
// audio playback (Unity does not support decoding arbitrary in-memory
// MP3 byte arrays directly into an AudioClip on device/Quest builds).
// =====================================================================

[RequireComponent(typeof(AudioSource))]
public class StreamingAudioQueuePlayer : MonoBehaviour
{
    public AudioSource audioSource;

    public event Action OnPlaybackStarted;
    public event Action OnPlaybackFinished;

    string cacheFolder;

    string activeRequestId = "";
    int nextSequenceToPlay = 1;
    int highestKnownSequence = -1; // -1 = total not known yet (still streaming)
    bool responseComplete = false;
    bool cancelled = false;
    bool playbackLoopRunning = false;

    readonly Dictionary<int, AudioClip> readyClips = new Dictionary<int, AudioClip>();
    readonly HashSet<int> decodingInProgress = new HashSet<int>();

    void Awake()
    {
        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        cacheFolder = Path.Combine(Application.persistentDataPath, "voice_stream_cache");
        Directory.CreateDirectory(cacheFolder);
    }

    // =====================================================
    // LIFECYCLE
    // =====================================================

    public void BeginNewRequest(string requestId)
    {
        StopAllCoroutines();

        ClearReadyClips();

        activeRequestId = requestId;
        nextSequenceToPlay = 1;
        highestKnownSequence = -1;
        responseComplete = false;
        cancelled = false;
        playbackLoopRunning = false;

        if (audioSource != null && audioSource.isPlaying)
            audioSource.Stop();

        if (audioSource != null)
            audioSource.clip = null;
    }

    public void EnqueueAudio(string requestId, int sequence, byte[] mp3Bytes)
    {
        if (cancelled || requestId != activeRequestId)
            return;

        StartCoroutine(DecodeAndStore(requestId, sequence, mp3Bytes));
    }

    public void MarkComplete(string requestId, int totalSequences)
    {
        if (requestId != activeRequestId)
            return;

        highestKnownSequence = totalSequences;
        responseComplete = true;

        EnsurePlaybackLoopRunning();
    }

    public void CancelPlayback()
    {
        cancelled = true;
        responseComplete = true;

        StopAllCoroutines();

        if (audioSource != null && audioSource.isPlaying)
            audioSource.Stop();

        if (audioSource != null)
            audioSource.clip = null;

        ClearReadyClips();

        playbackLoopRunning = false;
    }

    void ClearReadyClips()
    {
        foreach (var clip in readyClips.Values)
        {
            if (clip != null)
                Destroy(clip);
        }

        readyClips.Clear();
        decodingInProgress.Clear();
    }

    void EnsurePlaybackLoopRunning()
    {
        if (!playbackLoopRunning && !cancelled)
        {
            playbackLoopRunning = true;
            StartCoroutine(PlaybackLoop());
        }
    }

    // =====================================================
    // DECODE
    // =====================================================

    IEnumerator DecodeAndStore(string requestId, int sequence, byte[] mp3Bytes)
    {
        decodingInProgress.Add(sequence);

        string safeRequestId = string.IsNullOrEmpty(requestId) ? "req" : requestId;
        string path = Path.Combine(cacheFolder, safeRequestId + "_" + sequence + ".mp3");

        try
        {
            File.WriteAllBytes(path, mp3Bytes);
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to write streamed audio chunk to disk: " + e.Message);
            decodingInProgress.Remove(sequence);
            yield break;
        }

        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG))
        {
            yield return www.SendWebRequest();

            if (cancelled || requestId != activeRequestId)
            {
                decodingInProgress.Remove(sequence);
                yield break;
            }

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Failed to decode streamed audio seq=" + sequence + ": " + www.error);
                decodingInProgress.Remove(sequence);
                yield break;
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(www);

            if (cancelled || requestId != activeRequestId)
            {
                if (clip != null)
                    Destroy(clip);

                decodingInProgress.Remove(sequence);
                yield break;
            }

            readyClips[sequence] = clip;
        }

        decodingInProgress.Remove(sequence);

        EnsurePlaybackLoopRunning();
    }

    // =====================================================
    // PLAYBACK
    // =====================================================

    IEnumerator PlaybackLoop()
    {
        bool announcedStart = false;

        while (!cancelled)
        {
            bool weKnowThereIsNoMoreAfterThis =
                responseComplete &&
                highestKnownSequence >= 0 &&
                nextSequenceToPlay > highestKnownSequence;

            if (weKnowThereIsNoMoreAfterThis)
                break;

            bool clipReady =
                readyClips.TryGetValue(nextSequenceToPlay, out AudioClip clip) &&
                clip != null;

            bool sourceFree =
                audioSource != null && !audioSource.isPlaying;

            if (clipReady && sourceFree)
            {
                if (!announcedStart)
                {
                    announcedStart = true;
                    OnPlaybackStarted?.Invoke();
                }

                readyClips.Remove(nextSequenceToPlay);

                audioSource.clip = clip;
                audioSource.Play();

                int playedSequence = nextSequenceToPlay;
                nextSequenceToPlay++;

                while (audioSource != null && audioSource.isPlaying && !cancelled)
                {
                    yield return null;
                }

                Destroy(clip);

                if (cancelled)
                    yield break;
            }
            else
            {
                yield return null;
            }
        }

        playbackLoopRunning = false;

        if (!cancelled)
        {
            OnPlaybackFinished?.Invoke();
        }
    }
}
