using UnityEditor;
using UnityEngine;

/// <summary>Import rules:
///  • Resources/JogadorTripo.fbx → Humanoid avatar built from its T-pose (any humanoid animation can drive it).
///  • Resources/Animacoes/*.fbx (DeepMotion, Mixamo, mocap…) → Humanoid clips; idle/run loop.</summary>
public class CharacterImport : AssetPostprocessor
{
    void OnPreprocessModel()
    {
        var m = (ModelImporter)assetImporter;
        if (assetPath.Contains("Resources/JogadorTripo"))
        {
            m.globalScale = 1f; m.useFileScale = true; m.importAnimation = false;
            m.animationType = ModelImporterAnimationType.Human;
            m.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            m.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
        }
        else if (assetPath.Contains("Resources/Animacoes/"))
        {
            m.animationType = ModelImporterAnimationType.Human;
            m.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            m.importAnimation = true; m.materialImportMode = ModelImporterMaterialImportMode.None;
        }
    }

    void OnPreprocessTexture()
    {
        if (!assetPath.Contains("Resources/JogadorTripo_tex/")) return;
        var t = (TextureImporter)assetImporter;
        if (System.IO.Path.GetFileNameWithoutExtension(assetPath).EndsWith("_n")) t.textureType = TextureImporterType.NormalMap;
        t.maxTextureSize = 2048;
    }

    void OnPreprocessAnimation()
    {
        if (!assetPath.Contains("Resources/Animacoes/")) return;
        var m = (ModelImporter)assetImporter;
        string n = System.IO.Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();
        bool loop = n.StartsWith("corrida") || n.StartsWith("espera");
        var clips = m.defaultClipAnimations;
        foreach (var c in clips)
        {
            c.loopTime = loop; c.lockRootRotation = true; c.lockRootHeightY = true; c.lockRootPositionXZ = true;
            c.keepOriginalOrientation = true; c.keepOriginalPositionY = true; c.keepOriginalPositionXZ = true;
            c.name = n;
        }
        m.clipAnimations = clips;
    }
}
