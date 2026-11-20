# Démon 1 — moniteur d’identité et d’interaction humaine

Le démon 1 reconnaîtra le propriétaire visuellement et comportementalement, comparera les signaux à l’activité active du poste, estimera si une autre personne ou une automatisation manipule l’ordinateur et signalera les écarts au démon 3. Le démon 2 demeure responsable de la télémétrie de menace et des processus Windows natifs; le démon 3 corrèle et décide.

## Lots de travail

- [Mode_Apprentissage](Mode_Apprentissage/README.md) : calibration et stockage protégé du profil de comportement. C’est le premier lot implémenté.
- [Spécification générale](PROPOSITION_TECHNIQUE.md) : architecture cible, sources Windows, contrat D1→D3, seuil de divergence >60 et critères d’acceptation.

Le mode apprentissage reçoit actuellement des fenêtres agrégées et des états de vérification simulés. Le vrai collecteur clavier/souris et le vérificateur visuel restent à brancher. Ne pas présenter ce lot comme une reconnaissance d’identité complète.
