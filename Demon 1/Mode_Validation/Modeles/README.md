# Modèles locaux du module de vérification visuelle

Les poids ne sont pas ajoutés au dépôt Git. Ils sont récupérés une fois, avec versions et SHA-256 vérifiés, dans `%LOCALAPPDATA%\SentinelCore\Demon1Learning\models`.

Depuis PowerShell, à la racine `Demon 1` :

```powershell
powershell -ExecutionPolicy Bypass -File .\Mode_Validation\Modeles\Download-Models.ps1
```

Le moteur enchaîne YuNet (détection du visage), SFace (gabarit facial) et MiniFASNetV2 (contrôle d’une présentation papier/écran). Ces briques ne sont pas interchangeables : un visage trouvé n’établit pas une identité, et une bonne similarité ne prouve pas une présence vivante.

## Fichiers et licences indiquées par les dépôts amont

| Fichier | Usage | Licence amont | SHA-256 épinglé |
|---|---|---|---|
| `face_detection_yunet_2023mar.onnx` | Détecter un visage et ses repères | MIT (OpenCV Zoo) | `8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4` |
| `face_recognition_sface_2021dec.onnx` | Comparer deux représentations faciales | Apache-2.0 (OpenCV Zoo) | `0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79` |
| `MiniFASNetV2.onnx` | Détecter certaines présentations imprimées/rejouées | Code d’inférence Apache-2.0; poids issus de Silent-Face-Anti-Spoofing | `b32929adc2d9c34b9486f8c4c7bc97c1b69bc0ea9befefc380e4faae4e463907` |

Le seuil SFace de 0,363 est un point de départ publié pour LFW, pas un seuil de sécurité SentinelCore. Il devra être calibré sur plusieurs jours, plusieurs personnes, la webcam réelle, avec faux rejets et fausses acceptations mesurés. Le dossier amont de SFace affiche Apache-2.0 mais la provenance exacte des données d'entraînement de ce poids précis reste une question ouverte; ne pas redistribuer ce poids dans une version commerciale avant revue des droits. Le projet Silent-Face indique Apache-2.0 pour son code et ses ressources, mais la provenance des données d'entraînement du poids MiniFASNet reste à documenter séparément. MiniFASNet ne bloque pas toute usurpation : un résultat non clair devient `INDETERMINATE`, jamais `MATCH`.

Références : [OpenCV Zoo YuNet](https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet), [OpenCV Zoo SFace](https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface), [inférence MiniFASNet ONNX](https://github.com/yakhyo/face-anti-spoofing), [modèle d’origine](https://github.com/minivision-ai/Silent-Face-Anti-Spoofing).
