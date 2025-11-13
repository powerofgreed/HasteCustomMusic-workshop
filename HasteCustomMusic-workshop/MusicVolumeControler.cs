using Landfall.Haste.Music;
using UnityEngine;
using UnityEngine.Audio;

public class MusicVolumeController : MonoBehaviour
{
    private static MusicVolumeController _instance;
    private AudioMixer _mixer;
    private string _parameterName = "MusicVolume";
    private float _targetVolume = 1.0f;

    public static MusicVolumeController Instance
    {
        get
        {
            if (_instance == null)
            {
                var go = new GameObject("MusicVolumeController");
                _instance = go.AddComponent<MusicVolumeController>();
                DontDestroyOnLoad(go);
            }
            return _instance;
        }
    }

    private void Awake()
    {
        if (_instance == null)
        {
            _instance = this;
            DontDestroyOnLoad(gameObject);
            Initialize();
        }
        else if (_instance != this)
        {
            Destroy(gameObject);
        }
    }

    private void Initialize()
    {
        if (MusicPlayer.Instance?.DefaultMixer?.audioMixer != null)
        {
            _mixer = MusicPlayer.Instance.DefaultMixer.audioMixer;

            // Get current volume from mixer
            if (_mixer.GetFloat(_parameterName, out float currentDB))
            {
                _targetVolume = currentDB <= -80f ? 0f : Mathf.Pow(10f, currentDB / 20f);
            }

            Debug.Log($"[VolumeController] Initialized with volume: {_targetVolume * 100}%");
        }
        else
        {
            Debug.LogError("[VolumeController] Could not find MusicPlayer or mixer!");
            // Try again in 1 second
            Invoke("Initialize", 1f);
        }
    }

    void Update()
    {
        // Continuously enforce our target volume to prevent any resets
        ApplyVolume();
    }

    public float Volume
    {
        get => _targetVolume;
        set
        {
            _targetVolume = Mathf.Clamp01(value);
            ApplyVolume();
        }
    }

    private void ApplyVolume()
    {
        if (_mixer == null) return;

        float dB = _targetVolume <= 0.01f ? -80f : 20f * Mathf.Log10(_targetVolume);
        _mixer.SetFloat(_parameterName, dB);
    }
}