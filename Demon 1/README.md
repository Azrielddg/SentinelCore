# Démon 1 — moniteur d’identité et d’interaction humaine

Le démon 1 doit reconnaître le propriétaire visuellement et comportementalement, comparer ces signaux à l’activité active du poste, estimer si une autre personne ou une automatisation manipule l’ordinateur et signaler au démon 3 tout indice important. Il produit des observations et des scores explicables; le démon 3 prend les décisions de sécurité.

La spécification cible est dans [PROPOSITION_TECHNIQUE.md](PROPOSITION_TECHNIQUE.md). Le code présent sur cette branche est encore le noyau statistique initial : il ne capture pas encore clavier/souris, n’enrôle pas de visage, ne détecte pas les entrées injectées, et son seuil de publication n’est pas encore le seuil demandé de 60 %. Ne pas déployer ce prototype comme détecteur d’identité.

## Arborescence
- `src/SentinelCore.Demon1.Core`: moteur de profil, stockage et publication actuels.
- `src/SentinelCore.Demon1.Simulations`: simulation locale.
- `tests/SentinelCore.Demon1.Tests`: fixtures synthétiques et vérifications actuelles.

Les 50 scénarios synthétiques testent la logique, pas la performance biométrique terrain. Voir le document technique pour les composants à développer, les contrats D1→D3, les signaux Windows réutilisables et les tests d’acceptation requis.
