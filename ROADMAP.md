# Roadmap: Daily Cumulative Loss Indicator

## Vision

Construire un indicateur Quantower C# robuste qui affiche en temps reel la perte cumulative journaliere selon la logique Apex Trader Funding DLL, avec restauration fiable apres crash, redemarrage ou deconnexion.

L'indicateur doit rester simple dans son coeur mathematique: il echantillonne l'etat compte fourni par Quantower, conserve le plus haut niveau d'equity de la session, puis calcule la perte comme une soustraction d'etat.

## Etat Actuel

- [x] Projet Visual Studio cree.
- [x] `.gitignore` ajoute pour Visual Studio, .NET et caches locaux.
- [x] Squelette indicateur remplace par une base DCL en fenetre separee.
- [x] Parametres Quantower initiaux: compte, limite journaliere, heure de reset Paris.
- [x] Coeur de calcul `DclState` ajoute: equity, peak, DCL, remaining DLL, liquidation threshold.
- [x] `SessionClock` ajoute pour gerer le debut de session a 23:00 Europe/Paris.
- [x] Cache CSV local ajoute: restauration du dernier peak et append async sur nouveau peak.
- [x] Ecritures CSV chainees en file async pour conserver l'ordre des snapshots.
- [x] Lecture CSV resiliente: les lignes corrompues en fin de fichier sont ignorees.
- [x] Append CSV force sur `ClosedPositionAdded` quand la position cloturee appartient au compte suivi.
- [x] Reference locale `TradingPlatform.BusinessLayer.dll` configuree via projet Visual Studio.
- [x] Rendu HUD custom ajoute avec couleurs de risque et clignotement critique.
- [x] Axe Y relatif implemente: `0` = liquidation, `MaxDailyLoss` = room maximale, ligne principale = DLL restante.
- [x] Bandes de fond safe/warning/critical ajoutees dans le panel.
- [x] Courbe DLL restante colorisee par segments: vert en recuperation, rouge en degradation, crimson en zone critique.
- [x] Mode diagnostic optionnel ajoute au HUD: session, statut cache, nombre d'ecritures, derniere erreur I/O.
- [x] Fallback historique conservateur ajoute: reconstruction du peak realise depuis les positions cloturees de la session quand le cache est absent.
- [x] Alertes plateforme optionnelles ajoutees: warning a 50% restant, critique a 25%, une fois par session.
- [ ] Fallback historique complet avec replay bars/ticks intratrade.

## Principes Directeurs

- Calculer `DCL = DailyPeakBalance - CurrentEquity`, jamais par accumulation de ticks.
- Garder `OnUpdate()` en complexite `O(1)`.
- Ne jamais bloquer le thread UI avec de l'I/O fichier ou des requetes historiques.
- Persister uniquement les changements utiles: nouveau peak journalier ou evenement de cloture de trade.
- Preferer la tolerance aux erreurs a l'arret brutal de Quantower.
- Separer clairement le coeur metier, la persistence, la restauration historique et le rendu graphique.

## Definition Du Produit

### Parametres Utilisateur

- `Account`: compte Quantower selectionne.
- `MaxDailyLoss`: limite journaliere maximale en devise du compte.
- `ResetTime`: heure de reset de session, par defaut `23:00 Europe/Paris`.
- `CacheDirectory`: dossier de persistence CSV.
- `EnableHistoricalRecovery`: active ou desactive le fallback historique.
- `HudEnabled`: affiche ou masque le bloc texte.
- `FlashingAlertEnabled`: active le clignotement rouge sous 25% restant.
- `PlotEquityLine`, `PlotPeakLine`, `PlotLiquidationBarrier`: toggles de rendu.

### Sorties Visuelles Attendues

- Courbe d'equity temps reel.
- Courbe du peak journalier en escalier.
- Barriere de liquidation `DailyPeakBalance - MaxDailyLoss`.
- HUD affichant:
  - `DLL remaining`
  - `DCL current`
  - `Daily peak`
  - `Current equity`
- Coloration:
  - Vert si `remaining > 50%`
  - Orange si `25% <= remaining <= 50%`
  - Rouge clignotant si `remaining < 25%`

## Architecture Cible

### Modules

- `DailyCumulativeLossIndicator`
  - Classe Quantower principale.
  - Gere le cycle de vie `OnInit`, `OnUpdate`, `OnPaintChart`, `OnClear`.

- `DclState`
  - Etat courant de la session.
  - Champs principaux: balance, open PnL, current equity, daily peak, DCL, remaining DLL, timestamps.

- `SessionClock`
  - Calcule le debut de session selon `23:00 Europe/Paris`.
  - Gere le changement de jour et le reset de `DailyPeakBalance`.

- `CsvCacheStore`
  - Lecture du dernier etat persiste.
  - Append asynchrone des snapshots.
  - Creation du header CSV.
  - Gestion des erreurs fichier sans crash.

- `HistoricalRecoveryEngine`
  - Reconstitue le peak intraday si aucun cache du jour n'existe.
  - Lit les trades clotures depuis le debut de session.
  - Rejoue les intervalles de prix quand l'API Quantower le permet.

- `DclRenderer`
  - Rendu des courbes et du HUD.
  - Conversion temps/prix vers coordonnees du panel.
  - Gestion des couleurs, lignes, texte et clignotement.

## Phase 0: Validation Quantower Et Squelette

### Objectifs

- Creer un projet C# compatible avec les indicateurs Quantower.
- Verifier les references `TradingPlatform.BusinessLayer`.
- Confirmer les signatures exactes disponibles pour:
  - `Indicator`
  - `OnInit`
  - `OnUpdate`
  - `OnPaintChart`
  - `IAccount`
  - `Account.Balance`
  - `Account.OpenProfitLoss`
  - historique trades / ordres / positions
  - historical data

### Taches

- Creer la solution et le projet indicateur.
- Ajouter la classe principale `DailyCumulativeLossIndicator`.
- Declarer `SeparateWindow = true`.
- Ajouter les settings utilisateur minimum: compte et `MaxDailyLoss`.
- Compiler une version vide chargeable dans Quantower.

### Criteres D'Acceptation

- Le projet compile.
- L'indicateur apparait dans Quantower.
- L'indicateur peut etre attache a un graphique en fenetre separee.
- Aucun calcul metier n'est encore necessaire a cette phase.

## Phase 1: Coeur Mathematique Temps Reel

### Objectifs

- Implementer le calcul exact du DCL en temps reel.
- Garantir que la logique reste `O(1)` dans `OnUpdate()`.

### Taches

- Lire `selectedAccount.Balance`.
- Lire `selectedAccount.OpenProfitLoss`.
- Calculer `currentEquity = Balance + OpenProfitLoss`.
- Initialiser `dailyPeakBalance` au premier snapshot valide.
- Mettre a jour `dailyPeakBalance` uniquement si `currentEquity` le depasse.
- Calculer:
  - `currentDCL = dailyPeakBalance - currentEquity`
  - `remainingDLL = MaxDailyLoss - currentDCL`
  - `liquidationThreshold = dailyPeakBalance - MaxDailyLoss`
- Gerer les valeurs nulles, indisponibles ou non numeriques.

### Criteres D'Acceptation

- Le DCL augmente quand l'equity descend depuis le peak.
- Le DCL revient vers zero quand l'equity remonte vers le peak.
- Le peak ne baisse jamais dans une meme session.
- Aucun tick n'est accumule manuellement.
- `OnUpdate()` ne fait ni I/O fichier, ni requete historique.

## Phase 2: Gestion De Session Et Reset Journalier

### Objectifs

- Respecter le reset Apex a `23:00 Europe/Paris`.
- Eviter la contamination d'un jour de trading sur l'autre.

### Taches

- Implementer `SessionClock`.
- Convertir les timestamps UTC/local proprement.
- Calculer le `SessionStart` courant:
  - si l'heure locale est avant 23:00, debut de session = veille a 23:00
  - sinon debut de session = aujourd'hui a 23:00
- Detecter le passage a une nouvelle session.
- Reinitialiser `dailyPeakBalance` au premier `currentEquity` observe apres reset.
- Changer automatiquement le fichier CSV cible.

### Criteres D'Acceptation

- A 23:00 Paris, une nouvelle session commence.
- Le peak et le DCL sont reinitialises proprement.
- Le fichier cache utilise la date de session attendue.
- Les timestamps UTC et locaux sont coherents dans les logs.

## Phase 3: Persistence CSV Locale

### Objectifs

- Pouvoir restaurer l'etat apres crash ou redemarrage.
- Eviter tout blocage UI pendant l'ecriture.

### Taches

- Implementer le nommage:
  - `DailyCumulativeLoss_Cache_{AccountName}_{YYYYMMDD}.csv`
- Implementer le schema:
  - `Timestamp_UTC;Timestamp_Local;Balance;OpenPnL;CurrentEquity;DailyPeakBalance;DailyCumulativeLoss`
- Creer le fichier avec header si absent.
- Lire la derniere ligne valide au demarrage.
- Restaurer `dailyPeakBalance` depuis cette derniere ligne.
- Append un snapshot quand:
  - `dailyPeakBalance` augmente
  - un trade se cloture, si l'evenement est disponible
- Utiliser un verrou de fichier ou une file d'ecriture dediee.
- Catcher et logger les erreurs sans faire planter Quantower.

### Criteres D'Acceptation

- Un redemarrage intra-session restaure le dernier `dailyPeakBalance`.
- Les lignes CSV sont append-only.
- Une ligne corrompue n'empeche pas de lire une ligne precedente valide.
- Les ecritures fichier ne sont pas executees directement dans `OnUpdate()`.

## Phase 4: Historique En Memoire Pour Le Rendu

### Objectifs

- Dessiner les courbes sur le panel sans reconstituer l'historique a chaque repaint.
- Garder une consommation memoire raisonnable.

### Taches

- Creer une serie interne de points:
  - timestamp
  - current equity
  - daily peak
  - liquidation threshold
  - DCL
  - remaining DLL
- Ajouter un point quand le graphique avance ou quand une valeur significative change.
- Mettre en place une strategie de retention:
  - session courante uniquement par defaut
  - limite de points configurable si necessaire
- Recharger les points disponibles depuis le CSV au demarrage.

### Criteres D'Acceptation

- Le rendu peut afficher l'evolution depuis le debut de session.
- Le nombre de points reste borne ou raisonnable.
- Le repaint n'effectue aucun calcul lourd.

## Phase 5: Rendu Graphique Quantower

### Objectifs

- Offrir une lecture immediate du risque de liquidation.
- Synchroniser l'axe temps avec le graphique principal.

### Taches

- Implementer `OnPaintChart`.
- Dessiner la courbe d'equity:
  - segment vert si `E_t >= E_t-1`
  - segment rouge si `E_t < E_t-1`
- Dessiner la courbe `DailyPeakBalance` en step-line.
- Dessiner la barriere de liquidation en rouge crimson.
- Ajouter un axe relatif a droite si l'API le permet.
- Afficher le HUD en haut a droite.
- Implementer le clignotement rouge sous 25% restant sans bloquer le thread.
- Gerer les cas limites:
  - pas de compte selectionne
  - pas de donnees
  - `MaxDailyLoss <= 0`
  - panel trop petit

### Criteres D'Acceptation

- Les trois lignes sont visibles et lisibles.
- Le HUD change de couleur selon le risque.
- Le texte ne chevauche pas les elements principaux.
- L'indicateur reste fluide pendant les updates rapides.

## Phase 6: Fallback Historique Sans Cache

### Objectifs

- Reconstituer le peak journalier quand aucun CSV n'existe.
- Reduire le risque d'un faux niveau de DLL apres installation ou suppression du cache.

### Taches

- Identifier les APIs Quantower disponibles pour les trades clotures.
- Recuperer les executions/trades depuis `SessionStart`.
- Pour chaque trade cloture:
  - lire open time, close time, symbole, prix d'entree, sens, quantite
  - recuperer les barres historiques 1 minute ou ticks si possible
  - simuler l'equity flottante maximale pendant l'intervalle
- Integrer le peak simule au `dailyPeakBalance`.
- Prevoir un mode degradation:
  - si l'historique tick est indisponible, utiliser OHLC 1 minute
  - si les trades sont indisponibles, initialiser sur l'equity actuelle avec avertissement

### Criteres D'Acceptation

- Sans cache, le moteur tente une restauration historique.
- Les erreurs API n'arretent pas l'indicateur.
- Le mode degradation est explicite dans les logs.
- La restauration historique ne s'execute jamais dans `OnUpdate()`.

## Phase 7: Robustesse, Logs Et Observabilite

### Objectifs

- Rendre les erreurs diagnostiquables sans perturber l'utilisateur.

### Taches

- Ajouter une strategie de logs:
  - init
  - compte selectionne
  - session start
  - cache trouve / absent
  - restauration CSV
  - fallback historique
  - erreurs fichier
  - erreurs API historique
- Ajouter des compteurs internes:
  - nombre de snapshots CSV ecrits
  - dernier timestamp ecrit
  - dernier message d'erreur
- Exposer un etat minimal dans le HUD ou via logs Quantower.

### Criteres D'Acceptation

- Un probleme de cache peut etre diagnostique rapidement.
- Les exceptions sont capturees aux frontieres I/O et API.
- Le comportement par defaut reste utilisable meme en cas d'erreur.

## Phase 8: Tests Et Validation Manuelle

### Tests Unitaires

- `SessionClock`:
  - avant 23:00
  - apres 23:00
  - changement de date
  - fuseau Europe/Paris et DST
- `DclState`:
  - initialisation
  - hausse du peak
  - baisse d'equity
  - recuperation vers le peak
  - limite negative restante
- `CsvCacheStore`:
  - fichier absent
  - fichier valide
  - derniere ligne corrompue
  - append snapshot

### Tests D'Integration

- Lancer l'indicateur sans cache.
- Lancer l'indicateur avec cache existant.
- Simuler un crash puis redemarrage.
- Changer de compte.
- Changer `MaxDailyLoss`.
- Passer le reset de 23:00.

### Validation En Conditions Reelles

- Compte demo Quantower.
- Marche calme.
- Marche volatile.
- Position ouverte avec PnL flottant.
- Position cloturee.
- Deconnexion/reconnexion.

### Criteres D'Acceptation

- Les calculs correspondent a `Balance + OpenProfitLoss`.
- Le peak journalier est stable apres redemarrage.
- La limite restante est coherente avec Apex DLL.
- Aucun freeze visible pendant forte volatilite.

## Phase 9: Packaging Et Documentation

### Objectifs

- Rendre l'indicateur installable et maintenable.

### Taches

- Documenter l'installation dans Quantower.
- Documenter les parametres.
- Documenter l'emplacement des caches CSV.
- Ajouter une section troubleshooting:
  - cache absent
  - permissions fichier
  - compte non selectionne
  - historique indisponible
  - difference entre balance, equity et open PnL
- Ajouter une note claire sur la regle anti-accumulation.
- Preparer une release initiale.

### Criteres D'Acceptation

- Un utilisateur peut installer et configurer l'indicateur.
- Un developpeur peut reprendre le code sans relire toute l'architecture.
- Les limites connues sont documentees.

## Risques Techniques

- API Quantower differente selon version installee.
- Acces aux trades clotures potentiellement limite ou non standard.
- Historique tick indisponible selon broker/datafeed.
- `OpenProfitLoss` peut dependre de la devise, du symbole ou du mode de calcul broker.
- Gestion DST Europe/Paris sensible autour des changements d'heure.
- Ecriture fichier bloquee par antivirus, permissions ou chemin invalide.
- Rendu custom potentiellement contraint par les APIs `PaintChartArgs`.

## Decisions A Verifier Tot

- Source exacte des trades clotures dans Quantower.
- Evenement fiable de cloture de trade ou alternative par polling leger.
- Format exact des timestamps Quantower.
- Mode de conversion devise si positions multi-symboles / multi-devises.
- Capacite a dessiner un axe Y custom relatif dans une fenetre separee.
- Dossier de cache recommande pour un indicateur Quantower.

## Milestones

### M1: Indicateur Chargeable

- Projet compile.
- Indicateur visible dans Quantower.
- Settings de base disponibles.

### M2: Calcul Temps Reel

- Equity, peak, DCL et remaining DLL calcules correctement.
- Reset de session implemente.

### M3: Cache CSV

- Persistence append-only.
- Restauration apres redemarrage.
- Ecriture non bloquante.

### M4: Rendu Utilisable

- Courbes et HUD visibles.
- Couleurs de risque implementees.
- Comportement fluide en temps reel.

### M5: Recovery Historique

- Fallback sans cache fonctionnel.
- Modes degradation documentes.

### M6: Release Beta

- Tests principaux passes.
- Documentation d'installation disponible.
- Limites connues documentees.

## Backlog Priorise

### Priorite Haute

- Projet Quantower minimal.
- Settings compte et limite journaliere.
- Calcul `Balance + OpenProfitLoss`.
- Tracking du peak journalier.
- Reset session 23:00 Paris.
- Cache CSV append-only.
- Restauration depuis derniere ligne CSV.
- HUD remaining DLL / DCL.
- Barriere de liquidation.

### Priorite Moyenne

- Courbe d'equity coloree par segment.
- Courbe peak en escalier.
- Rechargement des points depuis CSV.
- Logs detailles.
- Detection d'evenement de cloture trade.
- Tests unitaires du coeur metier.

### Priorite Basse

- Fallback historique tick-level.
- Axe Y relatif custom avance.
- Options de theme visuel.
- Export diagnostic.
- Retention configurable multi-session.

## Definition Of Done Globale

- L'indicateur compile et se charge dans Quantower.
- Le calcul DCL suit strictement la formule `DailyPeakBalance - CurrentEquity`.
- Le peak est restaure apres redemarrage intra-session.
- Le reset journalier se produit a 23:00 Europe/Paris.
- Les erreurs de cache ou d'historique ne crashent pas Quantower.
- Le rendu affiche clairement equity, peak, liquidation threshold et remaining DLL.
- Les limites techniques connues sont documentees.
