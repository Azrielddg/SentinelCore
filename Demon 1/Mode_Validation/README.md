# Démon 1 — validation comportementale

## Objectif de cette première tranche

Comparer une période agrégée de clavier/souris au profil propriétaire construit par `Mode_Apprentissage`. Cette bibliothèque calcule un **indice de divergence** et un niveau de complétude/confiance des données. Elle ne collecte pas d’entrées, ne nomme pas une autre personne, n’exécute pas de mesures de réponse et ne contacte pas directement le démon 3.

## Règle de comparaison

- Le profil doit être `READY`; sinon le résultat est `INCONCLUSIVE` et code `PROFILE_NOT_READY`.
- La qualité de la période doit atteindre 0,80.
- Au moins deux mesures clavier et deux mesures souris doivent être disponibles; les données absentes ne sont jamais remplacées par un score neutre.
- Chaque mesure est comparée à la médiane du profil. L’échelle robuste prend la plus grande valeur entre MAD, IQR et une petite limite plancher, pour éviter une division par zéro. Les valeurs à moins d’un écart robuste valent 0; à quatre écarts ou plus, 100; entre les deux, l’indice monte linéairement.
- Les mesures clavier et souris pèsent chacune 50 %, pour éviter qu’un groupe avec plus de champs prenne tout le poids.
- `BEHAVIOR_DIFFERENT` est émis si l’indice dépasse 60 et que la qualité des données suffit. Ce 60 est un indice de divergence, **pas une probabilité d’intrusion**.

La formule est un prototype heuristique et `IsEmpiricallyValidated` reste `false`. Il ne faut pas présenter le seuil comme une précision acquise tant qu’il n’a pas été évalué sur des sessions réelles séparées par jour/utilisateur, avec FAR, FRR et EER.

## Ce que « distinguer deux utilisateurs » signifie pour l’instant

Le résultat compare une personne active au profil propriétaire. Il peut dire « comportement cohérent », « comportement différent » ou « données insuffisantes ». Il ne peut pas dire « c’est l’utilisateur B » : il n’existe pas encore de profils nommés séparés pour plusieurs personnes.

Le collecteur actuel apprend le profil lié au compte Windows et considère toute période capturée pendant une collecte propriétaire comme donnée propriétaire. Si une deuxième personne manipule le PC pendant cette collecte, ses mesures peuvent donc contaminer le profil. Avant une mesure de validation honnête, le profil propriétaire doit être gelé et les périodes de la deuxième personne doivent être recueillies séparément et étiquetées comme échantillons de validation, jamais ajoutées à l’apprentissage propriétaire.

## Contrat avec le démon 3

Si `ShouldNotifyDemon3` est vrai, `ToDemon3Payload` produit un payload `BEHAVIOR_DEVIATION` version 1, expirant après 30 secondes. Le démon 3 l’enveloppera avec les IDs et métadonnées du contrat inter-démons commun, puis utilisera son Named Pipe. Le champ `confidence` décrit uniquement la complétude/qualité des données et la maturité du profil; il n’est pas une probabilité. Le payload comprend les sous-scores clavier/souris, reason codes, aucune frappe brute et aucune référence de preuve par défaut. Le démon 3 reste l’unique propriétaire du Named Pipe, de la décision de risque finale et des actions.

## Limites et prochaine étape

Cette première tranche prend un `LearningReport` déjà construit et une période déjà agrégée. Elle n’est pas encore branchée au collecteur continu ni au Named Pipe. Prochaine étape : un adaptateur qui lit le profil propriétaire sans l’adapter pendant l’évaluation, envoie un `BEHAVIOR_DEVIATION` via l’interface de communication commune, puis collecte volontairement des sessions distinctes de propriétaire et de second utilisateur pour régler/mesurer le seuil.
