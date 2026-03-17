using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public class EditorTools : MonoBehaviour
{
    [MenuItem("工具/打开沙盒文件夹")]
    static void OpenPersistentDataPath()
    {
        System.Diagnostics.Process.Start(@Application.persistentDataPath);
    }

    [MenuItem("工具/生成游戏列表")]
    static void CreatGameListJson()
    {
        string path = Application.streamingAssetsPath + "/Game";
        DirectoryInfo root = new DirectoryInfo(path);
        GameList gameList = new GameList();
        foreach (FileInfo f in root.GetFiles())
        {
            if (f.FullName.Contains(".meta"))
            {
                continue;
            }
            string name = Path.GetFileNameWithoutExtension(f.FullName);
            gameList.Names.Add(name);
            Debug.Log(name);
        }
        string json = JsonUtility.ToJson(gameList);
        File.WriteAllText(Application.streamingAssetsPath + "/gamelist.json", json);
    }

    static string rootPath;

    [MenuItem("工具/打包WebGL")]
    public static void RunBuild()
    {
        CreatGameListJson();

        string nowTime = DateTime.Now.ToString("yyyy.MM.dd.HH.mm.ss");
        rootPath = Application.dataPath + "/../Build/Build" + nowTime + "/";
        if (!Directory.Exists(rootPath))
        {
            Directory.CreateDirectory(rootPath);
        }
        Build("bbkrpg-unity");
        System.Diagnostics.Process.Start(Application.dataPath + "/../Build/");
    }

    public static void Build(string sceneName)
    {
        if (!Directory.Exists(rootPath + "bbk/"))
        {
            Directory.CreateDirectory(rootPath + "bbk/");
        }
        BuildReport br = BuildPipeline.BuildPlayer(new[] { "Assets/Scenes/" + sceneName + ".unity" },
            rootPath + "bbk/", BuildTarget.WebGL, BuildOptions.None);
        if (br.files.Length < 0)
        {
            throw new Exception("BuildPlayer failure: " + br.strippingInfo);
        }
        else
        {
            //DirectoryInfo directoryInfo = new DirectoryInfo(rootPath + "bbk/");
            //directoryInfo.MoveTo(rootPath + sceneName + "/");
        }
    }

}