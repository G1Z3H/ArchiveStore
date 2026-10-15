# ArchiveStore

> Votre collection de jeux archivés, avec l'expérience d'un store : **Installer / Jouer / Désinstaller**, directement dans [Playnite](https://playnite.link).

ArchiveStore est une extension de bibliothèque pour Playnite. Elle transforme un simple dossier de jeux (un sous-dossier par jeu) en une vraie bibliothèque : les jeux apparaissent dans Playnite avec leur jaquette, leur taille, et les boutons natifs de Playnite, comme sur Steam ou Epic.

Version actuelle : **0.0.6** (développement actif, retours bienvenus).

---

## À quoi ça sert ?

Beaucoup de joueurs gardent une collection de jeux sur un disque dur ou un NAS : installeurs GOG hors ligne, jeux portables, archives. Pour y jouer, il faut les trouver, lancer le bon installeur, choisir un dossier, retrouver le bon `.exe`… et recommencer sur chaque PC.

ArchiveStore automatise tout cela :

- vous pointez l'extension vers votre ou vos dossiers d'archives ;
- chaque sous-dossier devient un jeu dans Playnite ;
- un clic sur **Installer** copie ou installe le jeu à l'endroit que vous avez choisi ;
- un clic sur **Désinstaller** retire la copie locale ;
- **votre archive d'origine n'est jamais modifiée.**

## L'objectif

L'idée est d'obtenir une **expérience de type console** pour une collection personnelle :

- **Zéro friction** : installer un jeu doit se faire en un clic. Avec **Installation directe**, aucune fenêtre ni question quand tout est clair ; sinon, de belles fenêtres vous laissent choisir.
- **Compatible manette et mode Plein écran** : les fenêtres de l'extension ont un style « console » et se pilotent à la manette (ou au clavier), en mode Plein écran comme en mode Bureau. Les menus, eux, sont dans le menu du jeu. Les **réglages** eux-mêmes ont une version « manette » (menu `Extensions`, voir plus bas).
- **Sécurité avant tout** : l'archive source reste intacte, et toute suppression est limitée au dossier de destination.
- **Pensé pour les collections nomades** : disque externe, Playnite portable, changement fréquent de PC.

---

## Fonctionnalités actuelles (0.0.6)

### Bibliothèque
- Détection automatique des jeux : **un sous-dossier d'une source = un jeu**.
- **Plusieurs profils**, chacun avec **une ou plusieurs sources** et **une ou plusieurs destinations**. Bascule de profil depuis `Bibliothèque > ArchiveStore > Basculer le profil` (menu principal : mode Bureau seulement, limite de Playnite).
- **Plusieurs sources** (plusieurs disques, un NAS…) : tous les jeux sont réunis dans la même bibliothèque. L'**ordre compte** : si le même dossier existe dans deux sources, la première gagne, et une notification vous signale le **doublon**.
- **Source inaccessible** (disque débranché, NAS éteint) : une notification le dit, les autres sources continuent de fonctionner.
- Statut **« installé »** détecté automatiquement si le jeu existe déjà dans l'une des destinations du profil.
- **Nom affiché nettoyé** : `Jeu_v1.2.3 [GOG]` devient `Jeu`, ce qui aide Playnite à retrouver jaquettes et descriptions. Les dossiers ne sont jamais renommés.
- **Jaquettes, icônes et fonds** : si le dossier d'un jeu contient `cover.jpg`, `icon.png` ou `background.jpg` (à la racine ou dans `_artwork`), ils sont utilisés en priorité.
- **Taille du jeu** inscrite dans le champ natif de Playnite, calculée en arrière-plan puis mise en cache.

### Plusieurs destinations
- Un profil peut avoir plusieurs destinations (par exemple un disque rapide, un disque externe, et le disque Windows pour un vieux jeu qui n'accepte que `C:`). **La première est la destination par défaut.** Un nom facultatif aide à s'y retrouver.
- **Avec fenêtres** (« Installation directe » décochée, en mode Bureau comme en mode Plein écran) : la fenêtre d'installation propose une liste de destinations, avec l'espace libre de chacune. Le choix est mémorisé pour ce jeu.
- **Choisir à l'avance** : clic droit sur un ou plusieurs jeux non installés (en mode Plein écran : menu du jeu, `Extensions`), puis `Choisir la destination d'installation (ArchiveStore)`. Rien n'est installé : le choix est mémorisé et sera utilisé à l'installation.
- **Installation directe** : aucune fenêtre, le jeu va dans la destination mémorisée, sinon dans la première.
- **Destination absente** (disque externe débranché) : une notification vous prévient à la mise à jour de la bibliothèque (désactivable). Si vous essayez d'installer vers un disque absent, rien n'est créé ni supprimé, et un message explique quoi faire.
- **Dossier de destination déjà existant** (installation ratée, jeu installé en dehors de Playnite, copie à la main…) : le plugin n'écrase jamais rien sans votre accord et **ne vide jamais un dossier**. Avec fenêtres (« Installation directe » décochée), une fenêtre affiche le chemin exact et propose trois choix :
  - **Utiliser ce dossier tel quel** : le jeu passe « installé » avec ce que le dossier contient ; rien n'est copié ni lancé. Proposé seulement si le dossier contient au moins un exécutable de jeu (sinon le bouton est grisé et la fenêtre dit pourquoi).
  - **Écraser** : l'installation (ou la copie) se fait par-dessus. Les fichiers du même nom sont remplacés, les autres sont conservés (mods, scripts ajoutés…), rien n'est supprimé. Avec un installeur, la fenêtre prévient qu'il peut remplacer des fichiers du dossier et qu'on ne peut pas garantir que les fichiers déjà présents seront conservés : faites une sauvegarde de ceux qui comptent.
  - **Annuler** : rien n'est modifié.
  Sur un dossier qui existait déjà, aucun nettoyage n'est fait en cas d'échec ou d'annulation, et la question « supprimer le dossier partiel ? » n'est pas posée. Avec **Installation directe**, aucune fenêtre : l'installation s'arrête avec un message qui explique les deux issues (décocher « Installation directe » pour choisir, ou choisir une autre destination), sans jamais écraser automatiquement.

### Installation
- Détection des installeurs **Inno Setup**, **NSIS** et **MSI** par **signature binaire** (et non par le nom du fichier).
- Installeur reconnu : installation **silencieuse** dans le dossier de destination. Sinon : **copie du dossier** (jeu portable).
- **Cas ambigu** : si aucun installeur n'est reconnu mais qu'un fichier ressemble à un installeur (nom commençant par `setup` ou `install`, plus les mots que vous ajoutez dans les paramètres), le plugin ne devine plus.
  - Une fenêtre propose **Installer** (l'installeur s'ouvre avec sa propre fenêtre), **Copier** (jeu portable) ou **Annuler**, en mode Bureau comme en mode Plein écran, même avec « Installation directe ».
  - Installation manuelle : le plugin vous indique le dossier exact à choisir dans l'installeur, attend sa fermeture, vous demande si l'installation est terminée et réussie, puis vérifie que le jeu est bien au bon endroit. Rien n'est jamais supprimé dans ce cas.
  - Mode Plein écran : la fenêtre ajoute un avertissement, car l'installeur a sa propre fenêtre, qui ne se commande pas à la manette : gardez un clavier et une souris à portée de main.
  - Une signature d'installeur reconnue reste toujours prioritaire.
- **DLC et patchs** (voir la section dédiée plus bas) : installés après le jeu, en silencieux, un par un.
- **Installation directe** (case des paramètres) : un clic sur Installer lance tout, sans fenêtre : le jeu va dans la destination par défaut du profil actif (ou celle choisie à l'avance), avec le mode et l'exécutable détectés automatiquement, puis tous les DLC et patchs reconnus. Les fichiers non reconnus ne sont jamais lancés. **Cette case décide seule**, que Playnite soit en mode Bureau ou Plein écran. Décochée, les fenêtres s'affichent dans les deux modes.
- **Taille et espace libre** affichés avant l'installation ; l'installation est refusée si la place manque, avec ce qu'il manque.
- **Progression** dans une fenêtre (avec fenêtres) ou dans une notification Playnite (installation directe), à un rythme réglable. Pour une copie, le bouton **Annuler** (ou la touche B de la manette) demande confirmation. Les fichiers partiels sont nettoyés en cas d'annulation ou d'échec.
- **Veille de Windows bloquée** pendant une installation, une copie ou l'installation des DLC et patchs : le PC ne s'endort pas tout seul au milieu d'une longue opération. Elle est de nouveau autorisée dès que l'opération se termine, même en cas d'erreur ou d'annulation. Cela n'empêche pas de mettre le PC en veille volontairement (fermeture du capot, bouton d'alimentation) et ne garde pas l'écran allumé.
- **Journal** détaillé dans `archivestore.log` (dossier de données de l'extension, `ExtensionsData` de Playnite) et dans `playnite.log`. Le bouton **« Ouvrir le journal »** des réglages ouvre le dossier et met le fichier en surbrillance.
- **Exécutable principal** : avec fenêtres, une fenêtre vous le fait choisir ; en installation directe, il est choisi automatiquement (le plus gros `.exe`, utilitaires écartés). Votre choix est **mémorisé** pour chaque jeu. Le menu `Réassocier l'exécutable` (clic droit sur le jeu, ou menu du jeu puis `Extensions` en mode Plein écran) permet de le corriger.
- **Exécutable qui a changé** (jeu mis à jour, exécutable renommé) : au clic sur Jouer, si l'exécutable lié a disparu, le plugin cherche dans le dossier d'installation. S'il n'y a **qu'un seul candidat**, il le prend ; s'il y en a **plusieurs**, la fenêtre de choix s'ouvre (votre choix est mémorisé) ; les désinstalleurs et autres utilitaires de la liste d'exclusions sont écartés. Le bouton Jouer est mis à jour et une notification vous demande de cliquer de nouveau sur Jouer (ce lancement-ci est annulé). Si rien n'est trouvé, le message d'erreur habituel s'affiche.

> **Installation directe et exécutable :** sans fenêtre, le choix automatique de l'exécutable peut se tromper. Le plus simple : choisissez-le une fois (fenêtre d'installation, ou `Réassocier l'exécutable`). Il est ensuite retenu, y compris après une réinstallation.

### Interface « console » et manette
- Les fenêtres de l'extension (installation, progression, choix de l'exécutable, de la destination, des DLC et patchs, confirmations) partagent un **style console** : sombre, lisible de loin, avec un pied de page qui rappelle les touches.
- **Manette** : croix directionnelle ou joystick gauche pour naviguer, **A** pour valider, **B** pour annuler, **X** et **Y** pour tout cocher ou tout décocher dans la fenêtre des DLC et patchs. En mode Plein écran, si Playnite inverse A et B dans ses réglages de manette, l'extension suit.
- **Clavier et souris** fonctionnent aussi : flèches, Entrée, Échap.
- Le bouton **A** est volontairement sans effet dans la fenêtre de progression : une pression distraite ne doit rien déclencher pendant une copie.
- En mode Bureau, la manette doit être activée dans les réglages d'entrée du mode Bureau de Playnite.
- **Réglages en mode manette** (nouveau en 0.0.6) : menu `Extensions` > **« ArchiveStore — Réglages »**, en mode Bureau comme en mode Plein écran. Une fenêtre à deux colonnes, entièrement pilotable à la manette (ou au clavier), reprend les réglages de l'extension en **neuf catégories** : Profils, Comportement, Notifications, Toasts, DLC et patchs, Installeurs non reconnus, Installation silencieuse, Exclusions et sauvegardes, Journal.
  - Les modifications sont faites **en direct** : **Y** enregistre, **B** annule (avec une confirmation si vous avez changé quelque chose). Les mêmes vérifications que dans les réglages classiques s'appliquent à l'enregistrement.
  - **Éditeur de profil** : nom, sources, destinations, profil actif, suppression.
  - **Navigateur de dossiers** pour choisir une source ou une destination : **gauche** remonte au dossier parent, **droite** entre dans le sous-dossier, **A** choisit, **Y** ouvre la boîte de dossier de Windows (pratique pour un chemin réseau), **B** annule.
  - Il n'y a **pas de saisie d'un chemin au clavier** : le clavier à l'écran de Playnite en mode Plein écran n'a pas la touche « \ ». Pour un chemin réseau, passez par la boîte de Windows (Y) ou par les réglages classiques.
  - Les réglages classiques (`Paramètres > Extensions > ArchiveStore`) restent disponibles en mode Bureau et contiennent les mêmes options.
- Une fenêtre ne réagit à la manette que lorsqu'elle est la fenêtre active : un appui sur B dans Playnite n'annule jamais une copie en arrière-plan.

### DLC et patchs
Rangez les DLC et les patchs dans des **sous-dossiers du dossier du jeu dont le nom commence par DEUX underscores (`__`)** :

```
E:\Archives
└── Cyberpunk 2077
    ├── setup_cyberpunk_2077.exe           ← le jeu
    ├── setup_cyberpunk_2077-1.bin         ← fichiers .bin GOG : à garder à côté de leur .exe
    ├── __DLC
    │   └── setup_cyberpunk_2077_phantom_liberty.exe
    └── __Patch
        ├── patch_01_cyberpunk_2077_1.63.exe
        └── patch_02_cyberpunk_2077_2.1.exe
```

Un DLC ou un patch peut aussi être un simple **dossier de fichiers**, sans installeur (voir « DLC et patchs en fichiers simples » plus bas) :

```
E:\Archives
└── Mon Jeu
    ├── Mon Jeu.exe
    ├── data\...
    └── __DLC
        └── Pack Texture HD               ← dossier de fichiers, sans installeur
            ├── data
            │   └── textures.pak          ← remplace data\textures.pak dans le jeu
            └── bonus.dll                 ← ajouté à la racine du jeu
```

- **La règle des deux underscores** (nouveau en 0.0.6) : un sous-dossier du jeu n'est un dossier d'extras que si son nom **commence par `__`** (deux underscores, sans espace avant) **et** contient :
  - **« DLC »** : c'est un dossier de DLC ;
  - **« patch »** (patch, patchs, patches, Patch_1.2… toutes les orthographes) ou **« update »** : c'est un dossier de patchs.
  Majuscules et minuscules n'ont pas d'importance. Exemples : `__DLC`, `__DLC - Pack HD`, `__Patch`, `__Patch_01`, `__Patches`, `__Update 1.2`. Le mot DLC, patch ou update est obligatoire : `__Pack` ne serait pas reconnu.
- **Pas de réglage, volontairement.** Beaucoup de jeux ont, dans leurs propres fichiers, un vrai dossier `DLC` ou `Patch` (des données du jeu). Sans les deux underscores, un dossier `DLC`, `Patch` ou `Update` est un dossier du jeu comme un autre : **il n'est jamais traité comme un extra**, et ArchiveStore n'y touche pas. Les deux underscores disent « ce dossier est un extra préparé pour ArchiveStore ».
- Ces dossiers ne sont jamais pris pour un jeu ni pour l'installeur du jeu de base. Seuls les sous-dossiers **directs** du dossier du jeu comptent (un `__DLC` rangé plus profond n'est pas vu).
- Après le jeu, les installeurs **reconnus** (Inno Setup, NSIS, MSI) sont lancés en silencieux, l'un après l'autre : **DLC d'abord, puis patchs**, chacun par ordre alphabétique. Les DLC et patchs **en fichiers simples** (sans installeur) sont **copiés** dans le dossier du jeu, dans le même ordre. **Dès qu'un échec survient, tout s'arrête** et le plugin dit lequel a échoué et lesquels n'ont pas été lancés.
- **Nommez vos patchs pour qu'ils se classent dans l'ordre alphabétique des versions** : numéro en tête avec deux chiffres (`patch_01…`, `patch_02…`, `patch_10…`) ou version complète et régulière. Sinon `1.10` passerait avant `1.9`.
- **Fichiers GOG** : gardez le `.exe` et ses fichiers `.bin` ensemble, dans le même dossier, avec leurs noms d'origine (ou renommez-les ensemble, en conservant le même début de nom). Les `.bin` ne sont pas lancés par le plugin : c'est l'installeur GOG qui les lit.
- **DLC et patchs en fichiers simples (nouveau en 0.0.6)** : un sous-dossier de DLC ou de patchs qui **ne contient aucun installeur** est une **copie de fichiers**. Son contenu est copié dans le dossier du jeu, **avec la même arborescence** : le dossier doit donc refléter celle du jeu (comme dans l'exemple plus haut).
  - Les fichiers du **même nom sont remplacés** (même s'ils sont en lecture seule, comme pour un patch qui remplace `jeu.exe`), les nouveaux fichiers sont **ajoutés**, **rien n'est supprimé**, et **la source n'est jamais modifiée**.
  - **Un dossier de premier niveau = un extra** : une case dans la fenêtre des DLC et patchs, libellée « (copie des fichiers) », avec la même mémoire « déjà installé » que les autres extras.
  - Un dossier est considéré comme **contenant un installeur** s'il a un installeur reconnu (Inno Setup, NSIS, MSI), ou un `.exe` dont le nom commence par `setup` ou `install` (ou l'un des mots que vous avez ajoutés dans « Installeurs non reconnus »). Il fonctionne alors comme avant. Dans un dossier à copier, les `.exe` font partie de la copie.
  - **Dossiers mixtes** : DLC avec installeur et DLC en fichiers peuvent cohabiter dans le même jeu ; chacun est traité selon son contenu.
  - En **Installation directe**, la copie se fait **sans confirmation**, en remplaçant les fichiers du même nom, comme pour les installeurs. Avec fenêtres, vous choisissez les extras à appliquer.
  - La progression d'une copie d'extra est approximative. Les `.bin` GOG ne sont jamais touchés.
- Un fichier `.exe` dont le type n'est pas reconnu n'est **jamais lancé tout seul**. Il est proposé dans la fenêtre de choix (décoché, avec un avertissement) et s'ouvre avec sa propre fenêtre ; ensuite le plugin vous demande si l'installation s'est bien passée. Attention : cela ne vaut que pour un dossier qui contient un installeur (reconnu, ou nommé `setup`/`install`…). Un installeur personnalisé au nom différent, rangé seul dans un dossier de DLC ou de patchs, serait **copié** comme un fichier ordinaire : ajoutez son mot dans « Installeurs non reconnus » pour qu'il soit traité comme un installeur.
- **Fenêtre de choix** (« Installation directe » décochée et « Demander confirmation avant d'installer les DLC et patchs » cochée, en mode Bureau comme en mode Plein écran) : une case par fichier pour choisir ce qui s'installe, et un bouton **« Déjà installés »** pour noter des extras installés avant cette version, sans rien réinstaller. Sans fenêtre (installation directe), tout s'installe automatiquement après le jeu.
- **Après coup** : clic droit sur un jeu installé (en mode Plein écran : menu du jeu, `Extensions`), puis `Installer les DLC et patchs (ArchiveStore)` (un jeu à la fois). Seuls les extras pas encore installés sont proposés. S'il n'y a rien à installer (aucun DLC ou patch trouvé, ou tout est déjà installé), un message l'indique, et un toast aussi (catégorie « Confirmations de menu »).
- **Nouveaux DLC et patchs détectés** : à chaque mise à jour de la bibliothèque, l'extension regarde, en arrière-plan, le dossier source de chaque jeu installé. S'il y a des DLC ou patchs qu'elle n'avait jamais vus, **une notification par jeu** l'indique (« 2 nouveaux DLC et 1 nouveau patch pour 'Mon jeu' »). Un **clic** ouvre la fenêtre des DLC et patchs de ce jeu, même avec « Installation directe » : rien ne s'installe sur un simple clic. Chaque fichier n'est signalé **qu'une seule fois**, même si vous ne l'installez pas. La première vérification d'un jeu ne signale rien : elle note seulement ce qui existe déjà. Seuls les jeux installés par un installeur reconnu, dont l'extension connaît les extras, sont surveillés. Les DLC et patchs en fichiers simples comptent aussi. Après avoir renommé vos dossiers en `__DLC` ou `__Patch`, **une notification de nouveaux DLC et patchs peut apparaître une fois** pour des extras déjà présents dans l'archive. Désactivable dans les réglages.
- Un **jeu portable** est copié en entier, DLC et patchs compris, sans aucune détection.
- Si l'espace manque pour le jeu et ses extras mais suffit pour le jeu seul, le jeu est installé et les extras sont ignorés (avec une notification).

### Désinstallation
- **Confirmation avant de désinstaller**, toujours demandée, même avec « Installation directe » : une fenêtre affiche le **chemin exact** qui sera supprimé avec tout son contenu (y compris les fichiers que vous y avez ajoutés), les dossiers de sauvegarde conservés, et rappelle que l'archive source n'est pas touchée. « Non » est sélectionné par défaut ; en répondant « Non », rien n'est supprimé et le jeu reste installé. Particulièrement utile pour un dossier repris « tel quel » ou « écrasé ».
- Lancement silencieux du désinstalleur du jeu s'il existe, puis suppression de la copie locale.
- **Dossiers de sauvegarde conservés** (liste configurable).
- La mémoire des DLC et patchs installés est effacée avec le jeu.
- Garde-fous : suppression refusée hors des dossiers de destination, jamais dans ou autour d'une source (toutes sources et destinations de tous les profils sont protégées) ; les jonctions et liens symboliques ne sont pas suivis.

### Notifications
Les notifications sont regroupées en six catégories, **toutes activées par défaut**, que vous pouvez désactiver une par une dans les réglages (groupe « Notifications ») :

1. **Nouveaux DLC et patchs** : voir plus haut.
2. **Source ou destination absente** : à la mise à jour de la bibliothèque, quand un disque ou un partage réseau n'est pas accessible. Pratique à décocher si vous débranchez volontairement un disque.
3. **Doublons entre sources** : le même dossier de jeu existe dans plusieurs sources (la première est utilisée).
4. **Résumés de fin** : jeu installé, désinstallé, installation annulée, DLC et patchs terminés.
5. **Infos pendant l'installation** : exécutable choisi ou mémorisé, destination de secours, avancement d'une installation directe. L'avancement d'une **copie** reste toujours affiché, car un clic dessus est le seul moyen de l'annuler sans fenêtre.
6. **Confirmations de menu** : profil activé, destination mémorisée, exécutable réassocié, et les messages « rien à installer » du menu `Installer les DLC et patchs`.

**Les erreurs et les blocages s'affichent toujours** (échec d'une installation, espace insuffisant, destination absente ou déjà remplie au moment d'installer, exécutable introuvable…) : ils ne se désactivent pas.

### Toasts
Un **toast** est une petite carte qui s'affiche en bas à droite de l'écran, au premier plan, **sans jamais prendre le focus** : elle apparaît même par-dessus un jeu en cours sans lui prendre le clavier ni la manette, puis disparaît toute seule après quelques secondes. Elle **s'ajoute** à la notification de Playnite, elle ne la remplace pas, et **ne se clique pas** : pour agir (par exemple ouvrir la fenêtre des nouveaux DLC et patchs), ouvrez la notification de Playnite. C'est une fenêtre dessinée par l'extension, pas une notification Windows.

- Groupe « Toasts » des réglages, **tout coché par défaut** : une case par catégorie de notification (les six ci-dessus) et une case **« Erreurs et blocages »** (les erreurs restent toujours en notification ; la case ajoute seulement un toast).
- Un toast n'apparaît que si la **notification correspondante est cochée** : la case toast est grisée sinon.
- **« Y compris pendant une partie »** (cochée par défaut) : décochée, un toast n'apparaît que si Playnite est au premier plan et qu'aucun jeu n'est en cours de lancement ou d'exécution.
- **Plusieurs toasts à la fois** : « L'un après l'autre » (par défaut : le suivant apparaît quand le précédent a disparu) ou « Superposés » (jusqu'à quatre à la fois, empilés). La file d'attente est limitée ; en cas d'inondation, les toasts en trop sont abandonnés, mais la notification existe toujours.
- Les messages longs sont raccourcis dans le toast ; le texte complet est dans la notification. L'avancement d'une copie et les messages secondaires sans catégorie ne font pas de toast.
- Le bouton **« Afficher des toasts de test »** montre trois toasts avec vos réglages actuels.

### Réglages
- Pour chaque profil : liste de **sources** (ordre = priorité) et liste de **destinations** (la première est celle par défaut), avec boutons Ajouter, Supprimer, ▲ et ▼.
- Les réglages sont refusés si une source et une destination sont identiques ou imbriquées, si deux destinations sont imbriquées, ou si un chemin est déclaré deux fois : c'est ce qui protège votre archive.
- Vos réglages d'une version précédente (une source, une destination) sont **repris automatiquement**.
- Arguments d'installation silencieuse modifiables (Inno Setup, NSIS, MSI).
- Liste d'exclusions d'exécutables (désinstalleurs, redistribuables, crash handlers…).
- Liste des dossiers de sauvegarde à conserver.
- **Installeurs non reconnus (cas ambigu)** : mots supplémentaires (un par ligne) et case « n'importe où dans le nom du fichier ». Ces mots ne servent qu'à déclencher la question du cas ambigu. Ils sont distincts de la liste d'exclusions, qui cache des fichiers : si un jeu portable contient un `setup.exe` qui n'est qu'un outil de réglages, ajoutez son nom aux exclusions.
- Cases à cocher : **Installation directe**, **Nettoyer les noms de jeux**, **Demander confirmation avant d'installer les DLC et patchs** (grisée quand « Installation directe » est cochée).
- Groupe **Notifications** : six cases (voir plus haut).
- Groupe **Toasts** : sept cases, « Y compris pendant une partie », la disposition (l'un après l'autre ou superposés) et un bouton de test (voir plus haut).
- Rythme de mise à jour de la progression (2 à 10 secondes).
- Bouton **Ouvrir le journal**.
- Tous ces réglages sont aussi accessibles à la manette par le menu `Extensions` > « ArchiveStore — Réglages » (voir « Interface console et manette »).

---

## Comment organiser vos archives

```
E:\Archives                 ← dossier Source (de votre profil)
├── Cyberpunk 2077
│   ├── setup_cyberpunk_2077.exe
│   ├── setup_cyberpunk_2077-1.bin
│   ├── __DLC               ← facultatif : DLC (installeurs ou fichiers)
│   ├── __Patch             ← facultatif : patchs (installeurs ou fichiers)
│   ├── cover.jpg           ← facultatif : jaquette
│   └── ...
├── Celeste                 ← jeu portable : le dossier est copié tel quel
│   └── Celeste.exe
└── ...

\\NAS\Jeux                  ← 2e source du même profil (facultatif)
└── ...

E:\Jeux                     ← dossier Destination par défaut (de votre profil)
├── Cyberpunk 2077          ← créé à l'installation
└── Celeste

C:\Jeux anciens             ← 2e destination (facultatif), pour un vieux jeu qui exige C:
└── ...
```

**Règle importante : le nom du dossier est l'identité du jeu.** Le lien entre un jeu de la bibliothèque et sa copie installée est le nom du dossier (`Destination\NomDuDossier`). Si vous renommez le dossier dans la Source, Playnite le verra comme un nouveau jeu. Avec plusieurs destinations, le jeu est retrouvé dans n'importe laquelle d'entre elles.

---

## Installation de l'extension

1. Téléchargez les fichiers de la dernière [release](../../releases) (`ArchiveStore.dll`, `extension.yaml`, `icon.png`).
2. Copiez-les dans un dossier `ArchiveStore` à l'intérieur du dossier `Extensions` de Playnite (version portable : `Playnite\Extensions\ArchiveStore`).
3. Redémarrez Playnite.
4. Ouvrez `Paramètres > Extensions > ArchiveStore`, créez un profil, ajoutez-y au moins une source et une destination, et choisissez-le comme profil actif.
5. Lancez `Bibliothèque > Mettre à jour les bibliothèques`.

Mise à jour depuis une version précédente : remplacez les fichiers de l'extension et redémarrez Playnite. Vos réglages sont conservés. Les nouvelles cases (toasts) sont **cochées par défaut** : après la mise à jour, des toasts apparaissent en plus des notifications ; décochez-les dans le groupe « Toasts » si vous n'en voulez pas. **Changement important de la 0.0.6 : les dossiers de DLC et de patchs doivent maintenant commencer par deux underscores** (`__DLC`, `__Patch`, `__Update`…). **Renommez vos dossiers `DLC`, `Patch` et `Update`** dans vos archives, sinon ils ne sont plus reconnus (voir « DLC et patchs »). Une fois renommés, leurs extras sont reproposés une fois, parce que la mémoire des extras installés retient leur ancien chemin : ouvrez `Installer les DLC et patchs (ArchiveStore)` sur le jeu, cochez ceux qui sont déjà installés et cliquez sur **« Déjà installés »**, sans rien réinstaller. Depuis la 0.0.6, les dossiers d'extras **sans installeur** sont copiés dans le jeu : vérifiez que les vôtres reflètent bien l'arborescence du jeu.

### Prérequis
- Playnite 10+ (SDK 6.17)
- Windows, .NET Framework 4.6.2

---

## Feuille de route

Ce qui suit est une liste d'**intentions**, sans engagement de date ni d'ordre.

### Prochaines étapes
- **Version anglaise** de l'extension (fenêtres, réglages, messages et documentation), prévue pour la **0.0.7**.
- **Préparation de la publication** : notes de version et README en anglais, captures d'écran, test sur un autre PC « propre », choix de l'icône.

### Idées pour la suite
- **Assistant de désinstallation** dans les paramètres : voir tous les jeux installés par l'extension, en cocher plusieurs et les désinstaller d'un coup.
- **Assistant « Nouveau PC »** : après un changement de machine, vérifier les profils et les lettres de disque, relier les jeux déjà installés, proposer les prérequis (Visual C++, DirectX, .NET).
- **Chemins relatifs** dans les profils, pour qu'un Playnite portable sur SSD externe survive aux changements de lettre de disque.
- **Détection des disques** par numéro de série ou nom de volume.
- **Mises à jour** : repérer qu'une archive a changé et ne recopier que les fichiers différents.
- **Vérification et réparation** d'une installation par rapport à l'archive ; **reprise** d'une copie interrompue.
- **Progression enrichie** : vitesse et durée restante.
- **Déplacer un jeu portable** vers une autre destination (pas pour les jeux posés par un installeur).
- **Listes de jeux connus et d'exclusions** livrées avec l'extension, pour mieux choisir l'exécutable en mode Plein écran, sans jamais écraser votre configuration.
- **Sauvegardes de jeux par jonction** (fonction facultative, désactivée par défaut), pour que les sauvegardes d'un jeu suivent l'utilisateur d'un PC à l'autre. À décider selon les retours des utilisateurs.
- **Rapport de santé** de la bibliothèque et bouton de diagnostic.
- **Respect du thème** de Playnite dans les paramètres.
- **Clavier à l'écran adapté** à la manette (avec la touche « \ »), pour pouvoir saisir un chemin réseau en mode Plein écran. Idée gardée de côté, pour une version ultérieure.

---

## Limites connues

- La **bascule de profil** est dans le menu principal de Playnite, qui n'existe qu'en **mode Bureau** : c'est une limite de Playnite. Les menus d'un jeu (réassociation d'exécutable, choix de la destination, DLC et patchs après coup) sont disponibles dans les deux modes ; en mode Plein écran, ils se trouvent sous le jeu, dans `Extensions`.
- Un installeur **non reconnu** ouvre sa propre fenêtre, que la manette ne sait pas piloter : en mode Plein écran, gardez un clavier et une souris à portée de main.
- La notification des **nouveaux DLC et patchs** reconnaît un fichier à son **chemin** dans le dossier du jeu : un patch remplacé par une version plus récente portant le même nom n'est pas signalé. Les jeux installés à la main (cas ambigu) et les jeux portables ne sont pas surveillés.
- Les jaquettes sont téléchargées par Playnite avec ses propres sources ; l'extension ne peut pas le forcer. Elle améliore seulement le nom envoyé et accepte vos images locales.
- Les installeurs exigeant les droits administrateur ne peuvent pas être silencieux. Pour Inno Setup, utilisez `PrivilegesRequired=lowest` dans votre script.
- Un installeur **non reconnu** est lancé avec sa propre fenêtre et ne peut pas être forcé vers le bon dossier : c'est à vous de choisir exactement le dossier indiqué par le plugin. Le plugin ne peut pas savoir à lui seul que l'installation est terminée, c'est pourquoi il vous pose la question.
- Les DLC et patchs doivent être rangés dans des sous-dossiers directs du jeu dont le nom commence par `__` (voir plus haut). Un dossier `DLC` ou `Patch` sans les deux underscores n'est pas reconnu, et un installeur de DLC qui s'y trouverait pourrait être pris pour l'installeur du jeu.
- Un jeu installé avant la 0.0.3 n'a pas de mémoire de ses DLC et patchs : le plugin les reproposera, et le bouton « Déjà installés » permet de les noter sans rien réinstaller. Tant que cette mémoire n'existe pas, le jeu n'est pas surveillé pour les nouveaux DLC et patchs.
- Les **toasts** ne se cliquent pas (ils ne prennent jamais le focus) : pour agir, ouvrez la notification de Playnite. Ils sont réservés aux notifications d'une catégorie et aux erreurs.
- La **veille bloquée** pendant une installation n'empêche que la veille automatique par inactivité : fermer le capot ou appuyer sur le bouton d'alimentation met toujours le PC en veille, et l'écran peut s'éteindre.
- **« Écraser »** sur un dossier existant ne supprime rien, mais un installeur reste libre de remplacer des fichiers du dossier selon ses propres règles (pour Inno Setup, la documentation dit qu'il ne remplace que ses propres fichiers, sans rien préciser des autres). Faites une sauvegarde de ce qui compte pour vous. Avec un installeur non reconnu, la vérification finale se contente de chercher un exécutable de jeu dans le dossier : elle réussit donc même si l'installeur a installé le jeu ailleurs.
- Après « **Utiliser ce dossier tel quel** » ou « Écraser », le plugin ne sait pas quels DLC et patchs se trouvent déjà dans le dossier ; ceux de l'archive pourront être reproposés. Et **désinstaller** ensuite supprime tout le dossier, y compris ce qui ne vient pas de l'archive : la confirmation affiche le chemin exact.
- **Réglages manette** : pas de saisie d'un chemin au clavier (le clavier à l'écran de Playnite n'a pas la touche « \ »). La boîte de dossier de Windows (Y) ou les réglages classiques permettent de saisir un chemin réseau.
- **DLC et patchs en fichiers simples** : le plugin ne vérifie pas que le dossier reflète l'arborescence du jeu, il copie ce qu'il trouve. Un fichier du même nom est toujours remplacé, sans sauvegarde de l'ancien ; la désinstallation du jeu supprime tout le dossier d'installation, extras copiés compris. La progression d'une copie d'extra est approximative.
- Les sauvegardes de jeux ne sont pas gérées par l'extension.

---

## Contribuer

Retours, idées et rapports de bugs sont les bienvenus dans les [Issues](../../issues). Pour un bug, indiquez la version d'ArchiveStore, la version de Playnite, le type d'installeur du jeu concerné et, si possible, un extrait du journal de l'extension (`archivestore.log`) ou de Playnite (`playnite.log`).

## Auteur

GizeH

## Licence

MIT. Voir le fichier [LICENSE](LICENSE).
