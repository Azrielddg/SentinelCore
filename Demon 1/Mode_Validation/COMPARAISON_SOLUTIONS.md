# Comparaison des solutions pour la vérification visuelle de D1

## Besoin SentinelCore

La vérification visuelle est déclenchée par un soupçon ou une alerte. Elle n’est pas dans le chemin critique de chaque événement : une réponse qui prend jusqu’à une minute est acceptable. Le traitement doit être local, fournir un résultat explicable à D3 et distinguer « correspond », « ne correspond pas » et « indéterminé ». La caméra ne remplace pas les signaux de menace fournis par D2.

## Comparaison des pistes

| Option | Ce qu’elle sait faire | Atouts pour SentinelCore | Limites à vérifier | Avis |
|---|---|---|---|---|
| Windows Hello | Authentifie un compte Windows avec la biométrie ou le PIN configuré dans Windows | Matériel et anti-usurpation gérés par Windows; bon facteur de réauthentification | L’application reçoit un résultat d’authentification, pas le gabarit facial ni un score de comparaison avec les profils SentinelCore | Garder comme signal facultatif de réauthentification; ce n’est pas notre moteur multiutilisateur |
| OpenCV Zoo YuNet + SFace | Détecte/aligne un visage puis calcule une représentation pour comparer des visages | Exécution locale; modèles ONNX; intégration .NET avec OpenCvSharp4; YuNet est sous MIT et SFace sous Apache-2.0 dans leurs dossiers de modèle OpenCV Zoo | SFace seul ne prouve pas la présence réelle; seuil d’un benchmark public à calibrer; les mainteneurs discutent encore de la provenance des données d’entraînement du modèle | Meilleur compromis actuel pour le prototype local, avec notice de licence et validation terrain |
| DeepFace | Cadre Python qui expose plusieurs modèles de comparaison faciale | Facile à expérimenter et comparer; licence MIT pour le code du cadre | Les licences des poids et dépendances varient; déploiement plus lourd en Python; comparer un visage n’est pas un contrôle de présence | Utile pour le banc d’essai, pas mon premier choix pour le binaire Windows final |
| InsightFace | Détection, alignement et reconnaissance faciale de haute qualité | Écosystème riche et très utilisé pour les expériences | Le code MIT ne confère pas automatiquement les droits sur les poids pré-entraînés; les modèles publics sont indiqués comme non commerciaux/recherche par défaut | Ne pas embarquer les poids publics sans autorisation/licence adaptée |
| Silent-Face-Anti-Spoofing | Classe une image de caméra comme visage réel ou présentation potentiellement fausse | Code Apache-2.0; ajoute une brique spécialisée pour repérer certaines photos/écrans | Projet et démonstration surtout orientés Android; performances annoncées par le dépôt et sensibles au capteur/scénario; ne donne pas l’identité | À évaluer comme brique anti-usurpation séparée, jamais comme preuve absolue |

## Comparaison du comportement et du contexte

| Solution | Signaux couverts | Ce que SentinelCore peut reprendre | Ce qu’elle ne résout pas |
|---|---|---|---|
| BioCatch | Rythme de frappe, souris, gestes et navigation dans des parcours numériques | Fusionner plusieurs familles de signaux et les traiter comme un indice passif complémentaire | Solution commerciale orientée services financiers/web; ne fournit pas un moteur local ouvert pour notre agent Windows |
| TypingDNA ActiveLock / API | Biométrie de frappe; ActiveLock annonce l’authentification continue du poste | Vérification répétée et score comportemental de frappe | La frappe seule ne contextualise pas un agent de code, un processus parent ou une hausse CPU; l’API cloud ne convient pas par défaut à nos données locales |
| Windows Sysmon + journaux Windows | Création de processus, parent, connexions, fichiers et événements système | Réutiliser les événements natifs pour attribuer « qui a lancé quoi » sans refaire un historique maison | Sysmon collecte des événements mais ne décide pas si le comportement est malveillant; D2 doit le consommer et le qualifier |
| SentinelCore D1 + D2 + D3 | Habitudes humaines, profils distincts, applications/contexte, ressources; événements de menace séparés; décision corrélée | Garder l’identité/contexte distincts du verdict technique et remettre les rapports explicables à D3 | Il faut encore collecter des sessions représentatives et mesurer les faux positifs/faux négatifs avant de revendiquer une performance |

L’approche recommandée est une fusion locale : D1 apprend l’usage habituel, D2 réutilise les journaux Windows et qualifie les événements techniques, puis D3 corrèle. On ne recode pas dans D1 l’arbre complet des processus déjà fourni par Windows/Sysmon. Sysmon fournit des événements; il ne décide ni ne bloque seul. [Microsoft détaille les événements Sysmon et leurs limites](https://learn.microsoft.com/en-us/windows/security/operating-system-security/sysmon/sysmon-events).

## Choix recommandé

1. Garder Windows Hello comme vérification native facultative du propriétaire du compte.
2. Faire un prototype local YuNet + SFace pour comparer les profils configurés, après vérification écrite de la licence des poids distribués.
3. Évaluer séparément un détecteur de présentation (photo papier, écran de téléphone, vidéo rejouée). Tant qu’il n’est pas testé sur notre matériel, D1 renvoie `liveness=UNKNOWN`.
4. Ne pas télécharger automatiquement des modèles au premier lancement. Versionner le nom, la somme SHA-256, la source, la licence et la version de chaque modèle livré.
5. Garder un statut à trois valeurs : `MATCH`, `NO_MATCH`, `INDETERMINATE`. Le seuil est appris/mesuré sur des sessions distinctes; aucune valeur de similarité brute ne devient une probabilité.

La tolérance d’une minute permet d’utiliser une séquence de plusieurs images, de rejeter les images floues et de retenter une capture sans bloquer le traitement de l’alerte. Pendant ce temps, D3 conserve le niveau de risque et applique ses règles urgentes; le résultat visuel arrive comme un élément supplémentaire.

## Comparaison pratique pour notre besoin

Les pourcentages ci-dessous sont une **estimation de couverture du besoin SentinelCore**, pas une précision biométrique mesurée. Ils évaluent le fonctionnement local Windows, l’usage multi-profil, la latence, l’intégration à D3, les licences du code/poids et la possibilité de conserver les preuves sur le PC.

| Solution | Couverture estimée pour D1 | Pourquoi |
|---|---:|---|
| Windows Hello seul | 48 % | Très bonne authentification Windows et protection anti-usurpation gérées par le système, mais SentinelCore ne reçoit ni gabarit, ni identité du visage, ni comparaison inter-profils exploitable pour un rapport après alerte. |
| YuNet + SFace + MiniFASNetV2 en local | 84 % | Détection, comparaison et anti-présentation séparés; fonctionnement hors ligne; profils chiffrés et preuve locale possibles. Reste à calibrer sur le PC et à tester contre les relectures vidéo et variations de lumière. |
| InsightFace / ArcFace | 72 % | Très bon écosystème de reconnaissance et modèles souvent utilisés pour de hautes performances; licence restrictive/non commerciale des poids publics et provenance à clarifier bloquent l’intégration directe du modèle public. |
| DeepFace | 65 % | Très pratique pour comparer plusieurs moteurs dans un banc d’essai; Python et licences de poids multiples alourdissent l’application Windows finale. |
| Service commercial BioCatch/TypingDNA | 52 % | Expertise comportementale utile, mais contrat, collecte distante éventuelle, dépendance fournisseur et intégration système ne répondent pas au besoin local de preuve visuelle du propriétaire. |

Ces chiffres indiquent quelle option convient le mieux au cahier des charges; ils ne signifient pas « 84 % de personnes reconnues correctement ». Une mesure de qualité devra utiliser un jeu local séparé par jour et par personne, publier FAR/FRR/EER, seuils, cas impossibles et conditions de caméra.

## Choix technique retenu dans cette branche

- **YuNet** : détecte un visage et fournit cinq repères. Le modèle YuNet 2023mar reste adapté à OpenCV 4.x; son entrée est de taille fixe. Le dépôt OpenCV Zoo indique MIT pour le dossier du modèle.
- **SFace** : calcule un vecteur de comparaison après alignement. Le modèle `face_recognition_sface_2021dec.onnx` est annoncé Apache-2.0 dans OpenCV Zoo. La somme SHA-256 est contrôlée par le script de récupération. Des questions publiques existent toutefois sur la provenance des données d’entraînement : conserver l’attribution, ne pas revendiquer une certification juridique et réévaluer si SentinelCore est commercialisé.
- **MiniFASNetV2** : classe la séquence comme vraie présence ou tentative d’image/relecture. Le code d’inférence ONNX de Yakhyo est sous Apache-2.0 et indique la classe 1 comme « Real ». Le projet dérive de Silent-Face-Anti-Spoofing. Ce contrôle est un indice, pas une garantie contre une attaque vidéo sophistiquée.
- **OpenCvSharp5.Windows** : wrapper .NET local sur OpenCV 5 qui expose FaceDetectorYN, FaceRecognizerSF et DNN/ONNX. MiniFASNet tourne dans le même moteur CPU : pas besoin du paquet Microsoft ONNX Runtime de 176 Mo qui avait été envisagé pendant le prototype. Le projet épingle la version du paquet et OpenCV rappelle de libérer ses objets natifs avec `Dispose`.
- **Windows Hello** : reste une autre manière de confirmer le compte Windows, pas l’identification faciale des profils SentinelCore. Ses gabarits biométriques ne sont pas livrés à l’application.

Le seuil SFace 0,363 cité par OpenCV correspond à un protocole LFW. Le code le fournit uniquement comme seuil provisoire configurable; il n’est ni un seuil final ni un score de risque.

## Fonctionnement cible, en exemple

1. D2 détecte qu’un programme inconnu lance une série de téléchargements et envoie son événement à D3.
2. D3 publie une demande de vérification visuelle portant un `correlationId`, un `sessionId` et une échéance pouvant aller jusqu’à 60 secondes.
3. D1 capture localement une courte séquence, vérifie sa qualité et compare le visage aux profils enrôlés.
4. D1 renvoie à D3 `MATCH` pour le profil le plus compatible, ou `NO_MATCH`/`INDETERMINATE`, le score calibré, la qualité, l’heure et une référence d’image chiffrée.
5. D3 combine ce résultat avec l’alerte de D2. Un profil reconnu peut réduire le soupçon d’usurpation d’identité; il ne met pas à zéro la gravité d’un téléchargement malveillant.

Si la demande expire, D3 reçoit `INDETERMINATE/TIMEOUT`; l’absence de caméra ne doit ni bloquer la collecte de l’événement D2, ni être interprétée comme une correspondance.

## Sources consultées

- Microsoft, [Windows Hello face authentication](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/windows-hello-face-authentication)
- Microsoft, [Windows Hello enhanced anti-spoofing et protection des modèles](https://learn.microsoft.com/en-us/windows/security/identity-protection/hello-for-business/policy-settings)
- Microsoft, [événements Sysmon et chaînes de processus](https://learn.microsoft.com/en-us/windows/security/operating-system-security/sysmon/sysmon-events)
- BioCatch, [signaux de biométrie comportementale](https://www.biocatch.com/resources/solution-brief/advanced-behavioral-biometrics)
- TypingDNA, [produits de vérification continue et biométrie de frappe](https://www.typingdna.com/)
- Recherche 2026, [évaluation inter-session de l’authentification par frappe](https://www.mdpi.com/2079-9292/15/11/2325)
- OpenCV, [YuNet et licence MIT](https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet)
- OpenCV, [SFace, résultats de benchmark et licence Apache-2.0](https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface)
- OpenCV, [tutoriel DNN visage et seuils de comparaison](https://docs.opencv.org/5.x/d0/dd4/tutorial_dnn_face.html)
- OpenCvSharp, [documentation/API .NET FaceDetectorYN et FaceRecognizerSF](https://shimat.github.io/opencvsharp/api/OpenCvSharp.FaceRecognizerSF.html)
- NuGet, [OpenCvSharp5.Windows](https://www.nuget.org/packages/OpenCvSharp5.Windows/)
- Yakhyo, [inférence MiniFASNet ONNX](https://github.com/yakhyo/face-anti-spoofing/blob/main/onnx_inference.py)
- OpenCV Zoo, [question publique sur la provenance SFace](https://github.com/opencv/opencv_zoo/issues/313)
- DeepFace, [dépôt et licence du code](https://github.com/serengil/deepface)
- InsightFace, [README et notes de licence des modèles](https://github.com/deepinsight/insightface#license)
- MiniVision, [Silent-Face-Anti-Spoofing](https://github.com/minivision-ai/Silent-Face-Anti-Spoofing)

## Décision d’implémentation

La reconnaissance visuelle ne doit pas être mise en production sur la base de la seule popularité GitHub. Le branchement de prototype est codé et les poids sont récupérés avec somme de contrôle; il reste à valider les seuils sur le matériel cible et à mesurer les faux rejets/acceptations. Comme une minute de latence est acceptable, cette étape reste hors du chemin critique de la décision technique D2.
