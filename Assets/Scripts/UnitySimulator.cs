using BBKRPGSimulator;
using BBKRPGSimulator.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
#if !UNITY_EDITOR && UNITY_WEBGL
using System.Runtime.InteropServices;
#endif

public class UnitySimulator : MonoBehaviour
{
    private RPGSimulator _simulator;
    public Image image;
    public Texture2D texture2D;
    public static UnitySimulator Instance;
    public GameObject ScrollView;
    public Toggle togglePrefab;

#if !UNITY_EDITOR && UNITY_WEBGL
    [DllImport("__Internal")]
    private static extern bool IsMobile();
#endif

    float currentTime = 0.3f;
    float invokeTime;

    private void Awake()
    {
        Instance = this;
        togglePrefab.gameObject.SetActive(false);
        image.gameObject.SetActive(false);
    }

    // Start is called before the first frame update
    void Start()
    {
        var isMobile = false;
#if !UNITY_EDITOR && UNITY_WEBGL
        isMobile = IsMobile();
#endif
        Debug.Log("isMobile:" + isMobile);
        StartCoroutine(Tool.LoadString(Application.streamingAssetsPath + "/gamelist.json", delegate (string json)
        {
            try
            {
                GameList gameList = JsonUtility.FromJson<GameList>(json);
                foreach (string name in gameList.Names)
                {
                    Toggle toggle = Instantiate(togglePrefab, togglePrefab.transform.parent);
                    toggle.name = name;
                    toggle.transform.Find("Label").GetComponent<Text>().text = name;
                    toggle.onValueChanged.AddListener((value) =>
                    {
                        if (value)
                        {
                            ScrollView.SetActive(false);
                            StartGame(name);
                            image.gameObject.SetActive(true);
                        }
                    });
                    toggle.gameObject.SetActive(true);
                }
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
        }));
    }

    void StartGame(string name)
    {
        Application.targetFrameRate = 60;
        Application.runInBackground = true;
        texture2D = new Texture2D(Constants.SCREEN_WIDTH, Constants.SCREEN_HEIGHT, TextureFormat.ARGB32, false);
        texture2D.filterMode = FilterMode.Point;
        texture2D.wrapMode = TextureWrapMode.Repeat;
        image.material.mainTexture = texture2D;
        _simulator = gameObject.AddComponent<RPGSimulator>();
        _simulator.RenderFrame += GameViewRenderFrame;
        string libPath = Application.streamingAssetsPath + "/Game/" + name + ".lib";
        Debug.LogWarning("libPath:" + libPath);
        StartCoroutine(Tool.LoadData(libPath, delegate (byte[] data)
        {
            if (data != null)
            {
                Debug.Log("GameName:" + Utilities.GetGameName(data));
                var options = new SimulatorOptions()
                {
                    LibData = data,
                };
                _simulator.Launch(options);
            }
            else
            {
                Debug.LogError("加载游戏lib失败");
            }
        }));
    }

    // Update is called once per frame
    void Update()
    {
        if (_simulator == null)
        {
            return;
        }
        //if (Input.GetMouseButtonDown(0))
        //{
        //    _simulator.KeyPressed(SimulatorKeys.KEY_ENTER);
        //}
        //if (Input.GetMouseButtonUp(0))
        //{
        //    _simulator.KeyReleased(SimulatorKeys.KEY_ENTER);
        //}

        if (Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            _simulator.KeyPressed(SimulatorKeys.KEY_ENTER);
        }
        if (Input.GetKeyUp(KeyCode.KeypadEnter))
        {
            _simulator.KeyReleased(SimulatorKeys.KEY_ENTER);
        }
        if (Input.GetKeyDown(KeyCode.Return))
        {
            _simulator.KeyPressed(SimulatorKeys.KEY_ENTER);
        }
        if (Input.GetKeyUp(KeyCode.Return))
        {
            _simulator.KeyReleased(SimulatorKeys.KEY_ENTER);
        }

        if (Input.GetKeyDown(KeyCode.Space))
        {
            _simulator.KeyPressed(SimulatorKeys.KEY_ENTER);
        }
        if (Input.GetKeyUp(KeyCode.Space))
        {
            _simulator.KeyReleased(SimulatorKeys.KEY_ENTER);
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            _simulator.KeyReleased(SimulatorKeys.KEY_CANCEL);
        }

        if (Input.GetKeyDown(KeyCode.UpArrow))
        {
            _simulator.KeyPressed(SimulatorKeys.KEY_UP);
        }

        if (Input.GetKeyDown(KeyCode.W))
        {
            _simulator.KeyPressed(SimulatorKeys.KEY_UP);
        }
        if (Input.GetKeyDown(KeyCode.S))
        {
            _simulator.KeyPressed(SimulatorKeys.KEY_DOWN);
        }
        if (Input.GetKeyDown(KeyCode.A))
        {
            _simulator.KeyPressed(SimulatorKeys.KEY_LEFT);
        }
        if (Input.GetKeyDown(KeyCode.D))
        {
            _simulator.KeyPressed(SimulatorKeys.KEY_RIGHT);
        }

        if (Input.GetKeyDown(KeyCode.LeftArrow))
        {
            _simulator.KeyPressed(SimulatorKeys.KEY_LEFT);
        }
        if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            _simulator.KeyPressed(SimulatorKeys.KEY_RIGHT);
        }
        if (Input.GetKeyDown(KeyCode.UpArrow))
        {
            _simulator.KeyPressed(SimulatorKeys.KEY_UP);
        }
        if (Input.GetKeyDown(KeyCode.DownArrow))
        {
            _simulator.KeyPressed(SimulatorKeys.KEY_DOWN);
        }


        if (Input.anyKey)
        {
            invokeTime += Time.deltaTime;
            foreach (KeyCode keyCode in Enum.GetValues(typeof(KeyCode)))
            {
                if (Input.GetKey(keyCode))
                {
                    invokeTime += Time.deltaTime;
                    if (invokeTime - currentTime > 0)
                    {
                        RepeatKey(keyCode);
                        invokeTime = 0;
                    }
                }
            }
        }
        else
        {
            if (invokeTime != currentTime)
            {
                invokeTime = currentTime;
            }
        }

        if (Input.GetKeyDown(KeyCode.F12))
        {
            Texture2D flipped = FlipTexture(texture2D, true, false);
            byte[] bytes = flipped.EncodeToPNG();
            File.WriteAllBytes(
                Application.persistentDataPath + "/"
                + DateTime.Now.ToFileTime() + ".png"
                , bytes);
        }
    }

    /// <summary>
    /// 翻转 Texture2D
    /// </summary>
    /// <param name="source">源纹理</param>
    /// <param name="flipVertical">是否上下翻转</param>
    /// <param name="flipHorizontal">是否左右翻转</param>
    /// <returns>翻转后的新 Texture2D</returns>
    private Texture2D FlipTexture(Texture2D source, bool flipVertical, bool flipHorizontal)
    {
        int width = source.width;
        int height = source.height;

        // 创建新纹理
        Texture2D flipped = new Texture2D(width, height, source.format, false)
        {
            filterMode = source.filterMode,
            wrapMode = source.wrapMode
        };

        // 获取像素
        Color[] pixels = source.GetPixels();
        Color[] flippedPixels = new Color[pixels.Length];

        // 翻转处理
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // 计算源坐标（根据翻转方向）
                int srcX = flipHorizontal ? (width - 1 - x) : x;
                int srcY = flipVertical ? (height - 1 - y) : y;

                // 复制像素
                flippedPixels[y * width + x] = pixels[srcY * width + srcX];
            }
        }

        // 应用像素
        flipped.SetPixels(flippedPixels);
        flipped.Apply();

        return flipped;
    }

    private void RepeatKey(KeyCode keyCode)
    {
        switch (keyCode)
        {
            case KeyCode.W:
            case KeyCode.UpArrow:
                _simulator.KeyPressed(SimulatorKeys.KEY_UP);
                break;

            case KeyCode.S:
            case KeyCode.DownArrow:
                _simulator.KeyPressed(SimulatorKeys.KEY_DOWN);
                break;

            case KeyCode.A:
            case KeyCode.LeftArrow:
                _simulator.KeyPressed(SimulatorKeys.KEY_LEFT);
                break;

            case KeyCode.D:
            case KeyCode.RightArrow:
                _simulator.KeyPressed(SimulatorKeys.KEY_RIGHT);
                break;
        }
    }

    private void GameViewRenderFrame(ImageBuilder frameData)
    {
        try
        {
            texture2D.LoadRawTextureData(frameData.Data);
            texture2D.Apply();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    private void OnApplicationQuit()
    {
        if (_simulator != null)
        {
            _simulator.Stop();
        }
    }
}

[Serializable]
public class GameList
{
    public List<string> Names = new List<string>();
}