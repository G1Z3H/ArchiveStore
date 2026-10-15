using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ArchiveStore
{
    // ======================================================================
    // ArchiveScanner — logique MÉTIER pure (scan, détection, copie, suppression).
    //
    // CONCEPT PÉDAGOGIQUE — séparation des responsabilités :
    // Cette classe ne dépend NI de WPF, NI de Playnite. C'est du C# "nu" :
    // on peut la tester toute seule, la réutiliser dans une console, etc.
    // Le plugin (ArchiveStorePlugin) fait le lien entre cette logique et
    // l'interface Playnite. Un bug de scan se corrige ICI, pas dans le plugin.
    // ======================================================================

    /// <summary>Type d'installeur détecté par SIGNATURE BINAIRE (pas par nom).</summary>
    public enum InstallerType
    {
        None,       // Aucun installeur → jeu portable (copie de fichiers)
        InnoSetup,  // Signature "Inno Setup" trouvée dans les octets
        Nullsoft,   // Signature "Nullsoft" trouvée dans les octets
        Msi,        // Fichier .msi (détection par extension, fiable pour MSI)
        Unknown     // Cas douteux : un exe "setup/install" dont le type n'est pas identifié
    }

    /// <summary>Résultat de l'analyse d'un dossier de jeu archivé.</summary>
    public class ScanResult
    {
        public string FolderName { get; set; }
        public string FullSourcePath { get; set; }
        public InstallerType DetectedType { get; set; }
        public string DetectedInstallerPath { get; set; }
        public bool IsInstalled { get; set; }
        public string InstallDirectory { get; set; }
        public string MainExePath { get; set; }

        /// <summary>
        /// Dossier de destination prévu pour CE jeu (Destination\NomDuJeu).
        /// Renseigné au moment de l'analyse avant installation.
        /// </summary>
        public string DestinationDirectory { get; set; }

        /// <summary>
        /// Taille totale du dossier source, en octets (0 = non calculée).
        /// Calculée seulement à l'installation (coûteux : parcourt tous les fichiers).
        /// </summary>
        public long SourceSizeBytes { get; set; }

        /// <summary>
        /// Espace libre sur le disque de destination, en octets.
        /// -1 = impossible à déterminer (on ne bloque alors jamais l'installation).
        /// </summary>
        public long FreeSpaceBytes { get; set; } = -1;

        /// <summary>Images (icône, jaquette, fond) trouvées dans le dossier source.</summary>
        public ArtworkFiles Artwork { get; set; }
    }

    /// <summary>
    /// Chemins des images trouvées dans le dossier d'un jeu archivé.
    /// Chaque chemin vaut null si l'image n'existe pas.
    /// </summary>
    public class ArtworkFiles
    {
        public string IconPath { get; set; }
        public string CoverPath { get; set; }
        public string BackgroundPath { get; set; }

        public bool HasAny
            => IconPath != null || CoverPath != null || BackgroundPath != null;
    }

    /// <summary>Donnée de progression remontée pendant une copie.</summary>
    public class CopyProgress
    {
        public string CurrentFile { get; set; }
        public string CurrentDirectory { get; set; }

        /// <summary>Total CUMULÉ d'octets copiés depuis le début de la copie.</summary>
        public long TotalBytesCopied { get; set; }
    }

    // ======================================================================
    // InlineProgress — IProgress "synchrone".
    //
    // PÉDAGOGIE : la classe standard Progress<T> poste le rappel sur le
    // SynchronizationContext capturé à sa création. Créée sur un thread
    // d'arrière-plan, elle utilise le pool de threads et les rappels peuvent
    // arriver DANS LE DÉSORDRE (la barre reculerait). InlineProgress appelle
    // le rappel tout de suite, sur le thread qui copie : l'ordre est garanti,
    // et c'est le plugin qui choisit comment rejoindre le thread UI
    // (Dispatcher).
    // ======================================================================
    public class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public InlineProgress(Action<T> handler) { _handler = handler; }
        public void Report(T value) { _handler?.Invoke(value); }
    }

    // ======================================================================
    // EXTRAS : un installeur de DLC ou de patch trouvé dans un sous-dossier
    // d'extras du jeu (voir ArchiveScanner.FindExtraInstallers).
    // ======================================================================
    public class ExtraInstaller
    {
        /// <summary>Chemin complet de l'installeur (dans l'archive source : lecture seule).</summary>
        public string Path { get; set; }

        /// <summary>Type reconnu par signature (Inno Setup, NSIS) ou extension (.msi).</summary>
        public InstallerType Type { get; set; }

        /// <summary>Nom du sous-dossier d'extras où il se trouve (ex. "DLC", "Patch").</summary>
        public string FolderName { get; set; }

        /// <summary>Vrai si le dossier est un dossier de DLC (son nom contient « DLC »), faux = patch.</summary>
        public bool IsDlc { get; set; }

        /// <summary>
        /// Vrai = installeur d'un type NON reconnu (ni Inno Setup, ni NSIS, ni MSI). Il n'est jamais lancé
        /// tout seul : seulement à la demande explicite de l'utilisateur, dans la fenêtre de choix, avec sa propre
        /// fenêtre (aucun argument silencieux : on ne sait pas lui en donner).
        /// </summary>
        public bool IsManual { get; set; }

        /// <summary>
        /// Vrai = « extra en fichiers » : un sous-dossier de DLC ou de patchs SANS installeur. Path est alors le
        /// DOSSIER lui-même (dans l'archive source : lecture seule). Il est copié dans le dossier du jeu avec la même
        /// arborescence : les fichiers du même nom sont remplacés, les autres ajoutés, rien n'est supprimé.
        /// </summary>
        public bool IsCopy { get; set; }

        /// <summary>
        /// Identifiant stable de cet installeur : son chemin RELATIF dans le dossier du jeu
        /// (ex. « DLC\setup_dlc1.exe »). C'est ce qu'on mémorise pour savoir qu'il est déjà installé.
        /// </summary>
        public string Key { get; set; }
    }

    public class ExtraScanResult
    {
        /// <summary>Installeurs reconnus, dans l'ordre d'exécution (DLC puis patchs).</summary>
        public List<ExtraInstaller> Installers { get; set; } = new List<ExtraInstaller>();

        /// <summary>
        /// .exe des dossiers d'extras dont le type n'est pas reconnu (chemins complets). Jamais lancés
        /// automatiquement ; voir Manual pour la possibilité de les lancer à la main.
        /// </summary>
        public List<string> NotRecognized { get; set; } = new List<string>();

        /// <summary>
        /// Les mêmes fichiers non reconnus, sous forme d'installeurs (IsManual = vrai), triés DLC puis
        /// patchs. Ils ne sont proposés que dans la fenêtre de choix, décochés, avec un avertissement.
        /// </summary>
        public List<ExtraInstaller> Manual { get; set; } = new List<ExtraInstaller>();

        /// <summary>Taille (archives) des dossiers de DLC contenant au moins un installeur reconnu.</summary>
        public long DlcBytes { get; set; }

        /// <summary>Taille (archives) des dossiers de patchs contenant au moins un installeur reconnu.</summary>
        public long PatchBytes { get; set; }

        /// <summary>
        /// Taille de chaque sous-dossier d'extras contenant un installeur reconnu (clé = nom du dossier,
        /// casse ignorée). Calculée UNE fois par le plugin ; sert à recalculer DlcBytes / PatchBytes
        /// quand une partie seulement des extras reste à installer.
        /// </summary>
        public Dictionary<string, long> FolderBytes { get; set; }
            = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Taille (archives) des dossiers des installeurs donnés. Un dossier n'est compté qu'UNE fois,
        /// même si on y a coché plusieurs installeurs : c'est donc une estimation, parfois un peu haute
        /// quand on ne choisit qu'une partie des fichiers d'un même dossier. Aucun accès disque.
        /// </summary>
        public long BytesFor(IEnumerable<ExtraInstaller> selected)
        {
            long total = 0;
            if (selected == null) return 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in selected)
            {
                if (x == null || string.IsNullOrEmpty(x.FolderName) || !seen.Add(x.FolderName)) continue;
                long size;
                if (FolderBytes != null && FolderBytes.TryGetValue(x.FolderName, out size)) total += size;
            }
            return total;
        }
    }

    public static class ArchiveScanner
    {
        // --- Signatures binaires recherchées au début des exécutables ---
        // Inno Setup et NSIS écrivent leur nom en clair dans leurs exécutables.
        // ATTENTION : ces chaînes ne sont PAS forcément dans les 4 premiers Ko
        // (l'en-tête PE seul en occupe déjà une bonne partie) : on lit donc
        // les premiers Mo, ce qui couvre le "stub" de l'installeur.
        // C'est BEAUCOUP plus fiable que le nom du fichier (un installeur
        // renommé "truc.exe" reste détectable).
        private static readonly byte[] InnoSignature = Encoding.ASCII.GetBytes("Inno Setup");
        private static readonly byte[] NsisSignature = Encoding.ASCII.GetBytes("Nullsoft");

        private const int SignatureScanBytes = 2 * 1024 * 1024; // 2 Mo

        private const int CopyBufferSize = 1024 * 1024;          // 1 Mo
        private const int ProgressIntervalMs = 100;              // anti-inondation de l'UI

        // Marge de sécurité exigée en plus de la taille du jeu (Windows, antivirus
        // et fichiers temporaires ont aussi besoin d'un peu de place) : 100 Mo.
        private const long SpaceSafetyMargin = 100L * 1000 * 1000;

        // --- Convention de nommage des images (jaquettes / icônes) ---
        // Le plugin cherche ces noms de fichiers (sans tenir compte de la casse)
        // à la racine du dossier du jeu, ou dans un sous-dossier "_artwork".
        // Sous-dossier prioritaire : il est explicite et évite de confondre avec
        // une image qui appartiendrait au jeu lui-même.
        private const string ArtworkSubFolder = "_artwork";
        private static readonly string[] IconNames = { "icon" };
        private static readonly string[] CoverNames = { "cover", "folder", "poster", "boxart", "jaquette" };
        private static readonly string[] BackgroundNames = { "background", "backdrop", "fanart", "bg", "hero" };
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };
        private static readonly string[] IconExtensions = { ".png", ".ico", ".jpg", ".jpeg", ".bmp" };

        // ==============================================================
        // SCÉNARIO 1 — SCAN DES SOURCES (une ou PLUSIEURS)
        // ==============================================================
        // Chaque sous-dossier de premier niveau d'une source = un jeu potentiel.
        //
        // PLUSIEURS SOURCES (plusieurs disques, un NAS…) : elles sont parcourues DANS L'ORDRE et
        // fusionnées en une seule liste de jeux. Si le même nom de dossier existe dans deux sources,
        // la PREMIÈRE gagne (l'autre est signalée dans le rapport, jamais perdue en silence).
        // Une source inaccessible (disque débranché, NAS éteint) est signalée dans le rapport ;
        // les autres continuent de fonctionner.
        //
        // PERFORMANCE : le scan tourne à CHAQUE mise à jour de bibliothèque.
        // On n'analyse donc PAS les installeurs ici (lecture récursive de
        // tout le dossier de chaque jeu = très lent sur un gros disque) :
        // l'analyse n'a lieu qu'au clic sur "Installer".
        /// <summary>Un jeu trouvé dans plusieurs sources : celle qui est gardée, et celle qui est ignorée.</summary>
        public class DuplicateGame
        {
            public string FolderName { get; set; }
            public string KeptSource { get; set; }
            public string IgnoredSource { get; set; }
        }

        public class SourcesScanReport
        {
            /// <summary>Jeux trouvés, toutes sources confondues (sans doublon de nom).</summary>
            public List<ScanResult> Results { get; } = new List<ScanResult>();

            /// <summary>Sources déclarées mais inaccessibles (chemins).</summary>
            public List<string> UnreachableSources { get; } = new List<string>();

            /// <summary>Phrases décrivant les doublons ignorés (pour le journal).</summary>
            public List<string> IgnoredDuplicates { get; } = new List<string>();

            /// <summary>Doublons ignorés, sous forme exploitable (pour la notification).</summary>
            public List<DuplicateGame> Duplicates { get; } = new List<DuplicateGame>();

            /// <summary>Nombre de sources réellement lues.</summary>
            public int ReachableCount { get; set; }
        }

        /// <param name="sources">Dossiers d'archives, par ordre de priorité.</param>
        /// <param name="destinationRoots">Toutes les destinations du profil (où chercher un jeu déjà installé).</param>
        /// <param name="plannedRootFor">
        /// Donne la destination PRÉVUE pour un jeu (nom de dossier → racine), ou null. Elle est testée en
        /// premier pour savoir si le jeu est installé, et sert de DestinationDirectory s'il ne l'est pas.
        /// </param>
        public static SourcesScanReport ScanSources(
            IEnumerable<string> sources,
            IEnumerable<string> destinationRoots,
            Func<string, string> plannedRootFor,
            List<string> excludedPatterns,
            CancellationToken token = default)
        {
            var report = new SourcesScanReport();
            var destRoots = (destinationRoots ?? Enumerable.Empty<string>())
                .Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // nom de dossier → source qui l'a pris

            foreach (var sourcePath in sources ?? Enumerable.Empty<string>())
            {
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(sourcePath)) continue;

                string[] subDirs;
                try
                {
                    if (!Directory.Exists(sourcePath))
                    {
                        report.UnreachableSources.Add(sourcePath);
                        continue;
                    }
                    subDirs = Directory.GetDirectories(sourcePath);
                }
                catch
                {
                    report.UnreachableSources.Add(sourcePath);
                    continue;
                }
                report.ReachableCount++;

                foreach (var dir in subDirs)
                {
                    token.ThrowIfCancellationRequested();

                    var folderName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(folderName)) continue;

                    string firstSource;
                    if (seen.TryGetValue(folderName, out firstSource))
                    {
                        report.IgnoredDuplicates.Add(
                            $"'{folderName}' trouvé dans '{sourcePath}' mais déjà présent dans '{firstSource}' : la première source gagne.");
                        report.Duplicates.Add(new DuplicateGame
                        {
                            FolderName = folderName,
                            KeptSource = firstSource,
                            IgnoredSource = sourcePath
                        });
                        continue;
                    }
                    seen[folderName] = sourcePath;

                    var result = new ScanResult
                    {
                        FolderName = folderName,
                        FullSourcePath = dir,
                        // Lecture légère (un seul listing du dossier du jeu) : acceptable
                        // à chaque mise à jour de la bibliothèque, contrairement à la taille.
                        Artwork = FindArtwork(dir)
                    };

                    // Garde-fou : Path.Combine("", "Jeu") donnerait un chemin RELATIF (donc relatif au
                    // dossier de Playnite !). Sans destination configurée, on ne calcule pas le statut "installé".
                    if (destRoots.Count > 0)
                    {
                        // Destination PRÉVUE d'abord (choix de l'utilisateur), puis toutes les autres :
                        // un jeu installé ailleurs que prévu est quand même reconnu comme installé.
                        string planned = null;
                        try { planned = plannedRootFor?.Invoke(folderName); } catch { }
                        var candidates = new List<string>();
                        if (!string.IsNullOrWhiteSpace(planned)) candidates.Add(planned);
                        foreach (var r in destRoots)
                            if (!candidates.Any(c => SamePath(c, r))) candidates.Add(r);

                        // Par défaut (jeu non installé) : le dossier prévu dans la destination prévue.
                        result.DestinationDirectory = Path.Combine(candidates[0], folderName);

                        foreach (var root in candidates)
                        {
                            var destDir = Path.Combine(root, folderName);
                            if (!Directory.Exists(destDir)) continue;

                            var exes = FindGameExes(destDir, excludedPatterns);
                            if (exes.Count == 0) continue;

                            result.IsInstalled = true;
                            result.InstallDirectory = destDir;
                            result.DestinationDirectory = destDir;
                            // Heuristique : le plus gros .exe "non auxiliaire" = exécutable principal.
                            result.MainExePath = PickBestExe(exes);
                            break;
                        }
                    }

                    report.Results.Add(result);
                }
            }

            return report;
        }

        /// <summary>
        /// Retrouve le dossier d'un jeu dans les sources, DANS L'ORDRE (la première qui le contient gagne).
        /// Retourne null si aucune source accessible ne le contient (jeu absent, ou source débranchée).
        /// </summary>
        public static string FindGameSourceDir(IEnumerable<string> sources, string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return null;
            // Un nom de dossier ne contient jamais de séparateur : on refuse « ..\x » et consorts.
            if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

            foreach (var src in sources ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(src)) continue;
                try
                {
                    var dir = Path.Combine(src, folderName);
                    if (Directory.Exists(dir)) return dir;
                }
                catch { /* chemin invalide ou source inaccessible : on passe à la suivante */ }
            }
            return null;
        }

        // ==============================================================
        // ÉNUMÉRATION SÛRE DES FICHIERS
        // ==============================================================
        // Directory.GetFiles(..., AllDirectories) s'arrête net (exception) au
        // premier sous-dossier inaccessible et suit les jonctions (risque de
        // boucle). Cette version ignore ce qui est inaccessible et ne suit
        // pas les points d'analyse (jonctions / liens symboliques).
        private static IEnumerable<string> EnumerateFilesSafe(string root, CancellationToken token = default)
        {
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var dir = pending.Pop();

                // NB : C# interdit "yield return" dans un try/catch → on remplit
                // les tableaux dans le try, puis on les parcourt en dehors.
                string[] files = null;
                try { files = Directory.GetFiles(dir); } catch { }
                if (files != null)
                    foreach (var f in files) yield return f;

                string[] subs = null;
                try { subs = Directory.GetDirectories(dir); } catch { }
                if (subs != null)
                    foreach (var s in subs)
                        if (!IsReparsePoint(s)) pending.Push(s);
            }
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return false; }
        }

        // ==============================================================
        // SCÉNARIO 2 (ÉTAPE A) — DÉTECTION DE L'INSTALLEUR
        // ==============================================================
        // Recherche récursive d'un installeur, identification par signature.
        // Les candidats sont examinés du plus SUPERFICIEL au plus profond :
        // l'installeur voulu est presque toujours à la racine ou juste dessous,
        // alors qu'un redistribuable embarqué (vcredist, PhysX…) est enfoui.
        //
        // Les sous-dossiers d'EXTRAS (DLC, patchs : voir IsExtraFolderName) sont IGNORÉS ici :
        // un installeur de DLC ne doit jamais être pris pour l'installeur du jeu de base.
        // (Les extras sont traités à part, voir FindExtraInstallers.)
        //
        // CAS AMBIGU (livraison B) : si AUCUN installeur n'est reconnu par signature dans tout le dossier,
        // un fichier dont le nom correspond à un MOT D'INSTALLEUR donne le type « Indéterminé ». Les mots
        // sont « setup » et « install » (écrits en dur) PLUS ceux de l'utilisateur (extraWords). Par
        // défaut seul le DÉBUT du nom compte ; avec wordsAnywhere, le mot peut être n'importe où.
        // La signature reste TOUJOURS prioritaire : un installeur reconnu est choisi avant tout fichier
        // « douteux », même s'il est plus profond dans l'arborescence.
        public static void DetectInstaller(
            string gameFolder,
            List<string> excludedPatterns,
            ScanResult result,
            List<string> extraWords = null,
            bool wordsAnywhere = false)
        {
            result.DetectedType = InstallerType.None;
            result.DetectedInstallerPath = null;

            try
            {
                var root = gameFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                var candidates = EnumerateFilesSafe(gameFolder)
                    .Where(f =>
                    {
                        var ext = Path.GetExtension(f);
                        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase);
                    })
                    .Where(f => !IsInExtraFolder(root, f))
                    .Where(f => !IsExcludedExe(f, excludedPatterns))
                    .OrderBy(f => f.Substring(root.Length).Count(c => c == Path.DirectorySeparatorChar))
                    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string doubtful = null; // premier "setup/install" non identifié

                foreach (var file in candidates)
                {
                    if (Path.GetExtension(file).Equals(".msi", StringComparison.OrdinalIgnoreCase))
                    {
                        result.DetectedType = InstallerType.Msi;
                        result.DetectedInstallerPath = file;
                        return;
                    }

                    var type = DetectInstallerTypeBySignature(file);
                    if (type == InstallerType.InnoSetup || type == InstallerType.Nullsoft)
                    {
                        result.DetectedType = type;
                        result.DetectedInstallerPath = file;
                        return;
                    }

                    // Le NOM n'identifie jamais le type (interdit par le cahier des
                    // charges) : il sert seulement à SIGNALER un cas douteux
                    // ("setup.exe" dont on ne reconnaît pas la signature).
                    if (doubtful == null && LooksLikeSetupName(file, extraWords, wordsAnywhere))
                        doubtful = file;
                }

                if (doubtful != null)
                {
                    // Cas "mixte/douteux" du cahier des charges → "Indéterminé".
                    result.DetectedType = InstallerType.Unknown;
                    result.DetectedInstallerPath = doubtful;
                    return;
                }

                // Des .exe existent mais aucun n'est un installeur
                // → ce sont des exécutables de jeu → jeu portable.
                result.DetectedType = InstallerType.None;
            }
            catch
            {
                result.DetectedType = InstallerType.None;
            }
        }

        // ==============================================================
        // EXTRAS (DLC, patchs) — sous-dossiers du dossier d'un jeu
        // ==============================================================
        // PÉDAGOGIE : l'utilisateur range les installeurs de DLC et de patchs dans
        // des sous-dossiers du jeu (ex. "MonJeu\DLC", "MonJeu\Patch"). Deux règles :
        //   1. Ces dossiers sont IGNORÉS quand on cherche l'installeur du jeu de base
        //      (sinon un DLC pourrait être pris pour le jeu).
        //   2. Une fois le jeu installé, on y cherche les installeurs à lancer ensuite.
        // Seuls les sous-dossiers DIRECTS du dossier du jeu comptent : un dossier
        // "Patch" enfoui plus profond fait partie du jeu lui-même et n'est pas touché.
        //
        // RÈGLE FIXE (plus de liste modifiable) : on regarde le NOM du dossier, sans tenir compte
        // des majuscules.
        //   - le nom contient « dlc »                      → DLC
        //   - le nom contient « patch » (patch, patchs, patches, Patch_1.2…) ou « update » → patch
        //   - sinon                                        → ce n'est pas un dossier d'extras

        /// <summary>Genre d'un sous-dossier du jeu.</summary>
        public enum ExtraFolderKind { None, Dlc, Patch }

        /// <summary>Classe un nom de dossier selon la règle fixe ci-dessus (DLC l'emporte sur patch).</summary>
        public static ExtraFolderKind ClassifyExtraFolder(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return ExtraFolderKind.None;
            if (folderName.IndexOf("dlc", StringComparison.OrdinalIgnoreCase) >= 0) return ExtraFolderKind.Dlc;
            if (folderName.IndexOf("patch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                folderName.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0)
                return ExtraFolderKind.Patch;
            return ExtraFolderKind.None;
        }

        /// <summary>Vrai si ce nom de dossier est un dossier de DLC ou de patchs.</summary>
        public static bool IsExtraFolderName(string folderName)
            => ClassifyExtraFolder(folderName) != ExtraFolderKind.None;

        /// <summary>
        /// Vrai si le fichier se trouve dans un sous-dossier DIRECT d'extras du dossier du jeu
        /// (ex. gameRoot\DLC\sous\fichier.exe). Un fichier posé directement à la racine du
        /// jeu, ou dans un autre sous-dossier, renvoie faux.
        /// </summary>
        public static bool IsInExtraFolder(string gameRoot, string filePath)
        {
            if (string.IsNullOrEmpty(gameRoot) || string.IsNullOrEmpty(filePath)) return false;

            var root = gameRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;

            var relative = filePath.Substring(root.Length)
                                   .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            int cut = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            if (cut <= 0) return false; // pas de sous-dossier : fichier à la racine du jeu

            return IsExtraFolderName(relative.Substring(0, cut));
        }

        /// <summary>
        /// Cherche les installeurs d'extras dans les sous-dossiers d'extras du jeu.
        /// - Installeurs RECONNUS (signature Inno Setup / NSIS, ou .msi) → Installers,
        ///   triés : dossiers de DLC d'abord, puis les patchs, chacun par ordre alphabétique.
        /// - Autres .exe → NotRecognized (jamais lancés, seulement signalés).
        /// - Les fichiers .bin d'un installeur GOG ne sont pas des .exe : ils sont ignorés
        ///   ici, c'est l'installeur lui-même qui les lit.
        /// Lecture seule : l'archive source n'est jamais modifiée.
        /// </summary>
        public static ExtraScanResult FindExtraInstallers(
            string gameFolder,
            List<string> excludedPatterns,
            List<string> extraInstallerWords = null,
            bool installerWordsAnywhere = false)
        {
            var result = new ExtraScanResult();
            if (string.IsNullOrEmpty(gameFolder)) return result;

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(gameFolder); }
            catch { return result; }

            var found = new List<ExtraInstaller>();
            var manual = new List<ExtraInstaller>();
            foreach (var sub in subDirs)
            {
                var folderName = Path.GetFileName(sub);
                var kind = ClassifyExtraFolder(folderName);
                if (kind == ExtraFolderKind.None) continue;
                if (IsReparsePoint(sub)) continue; // jamais de jonction suivie

                bool isDlc = kind == ExtraFolderKind.Dlc;

                // Ce sous-dossier a-t-il (au moins) un fichier ? Contient-il un installeur reconnu, ou un .exe qui
                // ressemble à un installeur (« setup »… : mêmes mots que le cas ambigu) ?
                bool hasAnyFile = false;
                bool hasRecognized = false;
                bool hasSetupLookingExe = false;
                var folderFound = new List<ExtraInstaller>();
                var folderManual = new List<ExtraInstaller>();
                var folderNotRecognized = new List<string>();

                foreach (var file in EnumerateFilesSafe(sub))
                {
                    hasAnyFile = true;

                    var ext = Path.GetExtension(file);
                    bool isMsi = ext.Equals(".msi", StringComparison.OrdinalIgnoreCase);
                    bool isExe = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
                    if (!isMsi && !isExe) continue;
                    if (IsExcludedExe(file, excludedPatterns)) continue;

                    var type = isMsi ? InstallerType.Msi : DetectInstallerTypeBySignature(file);

                    string key;
                    if (!TryGetRelativePath(gameFolder, file, out key))
                        key = folderName + Path.DirectorySeparatorChar + Path.GetFileName(file);

                    bool recognized = type == InstallerType.InnoSetup || type == InstallerType.Nullsoft || type == InstallerType.Msi;
                    var item = new ExtraInstaller
                    {
                        Path = file,
                        Type = type,
                        FolderName = folderName,
                        IsDlc = isDlc,
                        Key = key,
                        IsManual = !recognized
                    };

                    if (recognized)
                    {
                        hasRecognized = true;
                        folderFound.Add(item);
                    }
                    else
                    {
                        if (LooksLikeSetupName(file, extraInstallerWords, installerWordsAnywhere))
                            hasSetupLookingExe = true;
                        folderNotRecognized.Add(file);
                        folderManual.Add(item);
                    }
                }

                // RÈGLE : un dossier d'extras qui contient un installeur (reconnu, ou dont le nom ressemble à
                // « setup »…) fonctionne comme avant (lancement silencieux, ou lancement à la main). Un dossier SANS
                // aucun installeur est un « extra en fichiers » : son contenu est copié dans le dossier du jeu avec la
                // même arborescence. Ses éventuels .exe (ex. un jeu mis à jour) font alors partie de la copie.
                if (!hasRecognized && !hasSetupLookingExe)
                {
                    if (hasAnyFile)
                    {
                        string folderKey;
                        if (!TryGetRelativePath(gameFolder, sub, out folderKey)) folderKey = folderName;
                        found.Add(new ExtraInstaller
                        {
                            Path = sub,
                            Type = InstallerType.None,
                            FolderName = folderName,
                            IsDlc = isDlc,
                            Key = folderKey,
                            IsManual = false,
                            IsCopy = true
                        });
                    }
                    continue;
                }

                found.AddRange(folderFound);
                manual.AddRange(folderManual);
                result.NotRecognized.AddRange(folderNotRecognized);
            }

            result.Installers = found
                .OrderBy(x => x.IsDlc ? 0 : 1)
                .ThenBy(x => x.FolderName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.Manual = manual
                .OrderBy(x => x.IsDlc ? 0 : 1)
                .ThenBy(x => x.FolderName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.NotRecognized.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        // Mots d'installeur écrits en dur (jamais modifiables : l'utilisateur en AJOUTE d'autres).
        private static readonly string[] BuiltInInstallerWords = { "setup", "install" };

        // PÉDAGOGIE : « début du nom » = StartsWith ; « n'importe où » = IndexOf(...) >= 0. La comparaison
        // ignore la casse (OrdinalIgnoreCase) : Windows ne distingue pas « Setup » de « setup ».
        private static bool LooksLikeSetupName(string filePath, List<string> extraWords, bool anywhere)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrEmpty(name)) return false;

            foreach (var word in BuiltInInstallerWords)
                if (NameMatchesWord(name, word, anywhere)) return true;

            if (extraWords != null)
                foreach (var word in extraWords)
                    if (!string.IsNullOrWhiteSpace(word) && NameMatchesWord(name, word.Trim(), anywhere))
                        return true;

            return false;
        }

        private static bool NameMatchesWord(string name, string word, bool anywhere)
        {
            return anywhere
                ? name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0
                : name.StartsWith(word, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Lit le début d'un fichier (2 Mo max) et cherche une signature connue.
        /// Retourne None si aucune signature n'est reconnue (ou si la lecture échoue).
        /// </summary>
        public static InstallerType DetectInstallerTypeBySignature(string filePath)
        {
            try
            {
                byte[] buffer;
                int read = 0;
                // FileShare.ReadWrite : on lit même si le fichier est ouvert ailleurs.
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var size = (int)Math.Min(fs.Length, SignatureScanBytes);
                    buffer = new byte[size];
                    // Stream.Read peut renvoyer MOINS d'octets que demandé :
                    // on boucle jusqu'à avoir tout lu (ou la fin du fichier).
                    while (read < size)
                    {
                        int n = fs.Read(buffer, read, size - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }

                if (ContainsBytes(buffer, read, InnoSignature)) return InstallerType.InnoSetup;
                if (ContainsBytes(buffer, read, NsisSignature)) return InstallerType.Nullsoft;
                return InstallerType.None;
            }
            catch
            {
                return InstallerType.None;
            }
        }

        // Recherche naïve d'une sous-séquence dans les "length" premiers octets.
        private static bool ContainsBytes(byte[] haystack, int length, byte[] needle)
        {
            for (int i = 0; i <= length - needle.Length; i++)
            {
                if (haystack[i] != needle[0]) continue; // rejet rapide
                bool match = true;
                for (int j = 1; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }

        // ==============================================================
        // FILTRAGE DES UTILITAIRES
        // ==============================================================
        /// <summary>
        /// Vrai si le nom (sans extension) COMMENCE par l'un des motifs exclus
        /// (unins*, vc_redist, dxsetup…). Comparaison insensible à la casse.
        /// </summary>
        public static bool IsExcludedExe(string filePath, List<string> excludedPatterns)
        {
            if (excludedPatterns == null) return false;
            var name = Path.GetFileNameWithoutExtension(filePath);
            foreach (var pattern in excludedPatterns)
            {
                // Un motif vide ("".StartsWith("") est toujours vrai) exclurait TOUT.
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                if (name.StartsWith(pattern.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Tous les .exe "de jeu" (utilitaires exclus), récursif.</summary>
        public static List<string> FindGameExes(string folder, List<string> excludedPatterns)
        {
            try
            {
                return EnumerateFilesSafe(folder)
                    .Where(f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    .Where(f => !IsExcludedExe(f, excludedPatterns))
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        // ==============================================================
        // CHOIX AUTOMATIQUE DE L'EXÉCUTABLE PRINCIPAL
        // ==============================================================
        // Sans fenêtre de choix (mode console), il faut deviner le bon .exe.
        // Règle : on écarte les exécutables AUXILIAIRES connus (gestionnaires de
        // plantage, redistribuables…), puis on prend le plus GROS : le jeu est
        // presque toujours bien plus lourd que ses lanceurs/outils.
        // Ces préfixes sont en plus de la liste d'exclusions configurable : ils ne
        // servent QU'À départager, ils n'empêchent pas de choisir l'exe à la main.
        private static readonly string[] HelperExePrefixes =
        {
            "unitycrashhandler", "crashpad", "crashreport", "crashsender", "bugreport",
            "dotnet", "ndp", "dxwebsetup", "oalinst", "physx", "vcredist", "vc_redist",
            "dxsetup", "directx", "redist", "updater", "unins"
        };

        /// <summary>
        /// Meilleur candidat parmi des .exe de jeu : plus gros non-auxiliaire, sinon
        /// plus gros tout court. Retourne null si la liste est vide.
        /// </summary>
        public static string PickBestExe(List<string> exes)
        {
            if (exes == null || exes.Count == 0) return null;

            var primary = exes
                .Where(f => !IsHelperExe(f))
                .OrderByDescending(SafeGetFileSize)
                .FirstOrDefault();

            return primary ?? exes.OrderByDescending(SafeGetFileSize).First();
        }

        private static bool IsHelperExe(string filePath)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            return HelperExePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        // ==============================================================
        // NETTOYAGE DU NOM AFFICHÉ
        // ==============================================================
        // PÉDAGOGIE — pourquoi nettoyer ? Playnite cherche jaquettes et descriptions
        // en ligne À PARTIR DU NOM du jeu. "Cyberpunk.2077_v2.1 [GOG]" ne sera pas
        // reconnu, "Cyberpunk 2077" oui. On ne touche JAMAIS au nom du dossier (c'est
        // l'identifiant du jeu pour installer/désinstaller) : seul le nom AFFICHÉ change.
        //
        // Regex : "motif de recherche" de texte. Ici :
        //   [\[\(\{][^\]\)\}]*[\]\)\}]  = tout groupe entre [ ], ( ) ou { }
        //   version : "v1.2", ou 3 nombres séparés par des points "1.10.163"
        private static readonly Regex BracketGroups =
            new Regex(@"\s*[\[\(\{][^\]\)\}]*[\]\)\}]", RegexOptions.Compiled);

        private static readonly Regex VersionSuffix =
            new Regex(@"(?:[\s_-]+v\d+(?:\.\d+)*|[\s_-]+\d+(?:\.\d+){2,}|[\s_-]+build[\s_-]*\d+).*$",
                      RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex JunkWords =
            new Regex(@"\b(?:repack|portable|multi\d*|gog|fitgirl|dodi|codex|setup)\b",
                      RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ManySpaces =
            new Regex(@"\s{2,}", RegexOptions.Compiled);

        /// <summary>
        /// Nom d'affichage nettoyé. Si le nettoyage viderait le nom, on garde l'original.
        /// </summary>
        public static string CleanGameName(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return folderName;

            var s = folderName.Replace('_', ' ');
            s = BracketGroups.Replace(s, "");
            s = VersionSuffix.Replace(s, "");
            s = JunkWords.Replace(s, " ");
            s = ManySpaces.Replace(s, " ").Trim(' ', '-', '_', ',');

            return s.Length == 0 ? folderName : s;
        }

        // ==============================================================
        // GARDE-FOU "DESTINATION DÉJÀ PEUPLÉE"
        // ==============================================================
        /// <summary>
        /// Vrai si le dossier contient autre chose que les dossiers de sauvegarde
        /// protégés. Sans ça, après une désinstallation qui a CONSERVÉ des saves,
        /// le dossier ne serait plus jamais "vide" et le jeu ne pourrait plus
        /// être réinstallé.
        /// </summary>
        public static bool HasForeignContent(string dir, List<string> saveFolderNames)
        {
            if (!Directory.Exists(dir)) return false;

            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                bool isDir = Directory.Exists(entry);
                if (isDir && saveFolderNames != null &&
                    saveFolderNames.Any(s => Path.GetFileName(entry)
                        .Equals(s, StringComparison.OrdinalIgnoreCase)))
                    continue; // dossier de sauvegarde conservé : ne compte pas

                return true;
            }
            return false;
        }

        // ==============================================================
        // SCÉNARIO 2 (ÉTAPE C) — COPIE RÉCURSIVE AVEC PROGRESSION
        // ==============================================================
        // État partagé par tous les appels récursifs d'une même copie.
        private class CopyState
        {
            public long Copied;
            public DateTime LastReportUtc = DateTime.MinValue;
        }

        public static void CopyDirectory(
            string source,
            string destination,
            IProgress<CopyProgress> progress,
            CancellationToken token)
        {
            var state = new CopyState();
            CopyDirectoryInternal(source, destination, progress, token, state);
            // Rapport final forcé : la barre atteint bien 100 %.
            Report(progress, state, null, null, true);
        }

        private static void CopyDirectoryInternal(
            string source, string destination,
            IProgress<CopyProgress> progress, CancellationToken token, CopyState state)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.GetFiles(source))
            {
                token.ThrowIfCancellationRequested();
                var destFile = Path.Combine(destination, Path.GetFileName(file));
                CopyFileWithProgress(file, destFile, progress, token, state);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                token.ThrowIfCancellationRequested();
                // On ne suit pas les jonctions : risque de boucle infinie.
                if (IsReparsePoint(dir)) continue;
                var destDir = Path.Combine(destination, Path.GetFileName(dir));
                CopyDirectoryInternal(dir, destDir, progress, token, state);
            }
        }

        // Copie par blocs de 1 Mo : un seul fichier de 20 Go fait quand même
        // avancer la barre, et l'annulation est prise en compte à chaque bloc.
        private static void CopyFileWithProgress(
            string sourceFile, string destFile,
            IProgress<CopyProgress> progress, CancellationToken token, CopyState state)
        {
            // Un fichier de même nom déjà présent dans la destination (cas d'un DLC ou d'un patch copié PAR-DESSUS
            // le jeu) sera remplacé : s'il est en « lecture seule », Windows refuserait. On lève cet attribut seulement
            // sur ce fichier de la DESTINATION ; la source n'est jamais touchée.
            try
            {
                if (File.Exists(destFile) && (File.GetAttributes(destFile) & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(destFile, FileAttributes.Normal);
            }
            catch { /* si on n'y arrive pas, l'écriture échouera et l'erreur sera signalée */ }

            var buffer = new byte[CopyBufferSize];
            using (var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, n);
                    state.Copied += n;
                    Report(progress, state, Path.GetFileName(sourceFile),
                           Path.GetDirectoryName(sourceFile), false);
                }
            }

            try { File.SetLastWriteTimeUtc(destFile, File.GetLastWriteTimeUtc(sourceFile)); }
            catch { /* date non conservée : sans gravité */ }
        }

        private static void Report(
            IProgress<CopyProgress> progress, CopyState state,
            string file, string dir, bool force)
        {
            if (progress == null) return;
            var now = DateTime.UtcNow;
            if (!force && (now - state.LastReportUtc).TotalMilliseconds < ProgressIntervalMs)
                return;
            state.LastReportUtc = now;
            progress.Report(new CopyProgress
            {
                CurrentFile = file,
                CurrentDirectory = dir,
                TotalBytesCopied = state.Copied
            });
        }

        /// <summary>
        /// Taille totale d'un dossier (pour le % de progression et l'affichage
        /// "taille du jeu"). L'annulation est propagée (OperationCanceledException).
        /// </summary>
        public static long GetDirectorySize(string path, CancellationToken token = default)
        {
            long total = 0;
            try
            {
                foreach (var file in EnumerateFilesSafe(path, token))
                    total += SafeGetFileSize(file);
            }
            catch (OperationCanceledException) { throw; } // l'annulation ne doit pas être avalée
            catch { }
            return total;
        }

        // ==============================================================
        // ESPACE LIBRE SUR LE DISQUE
        // ==============================================================
        // PÉDAGOGIE — P/Invoke :
        // .NET ne sait pas tout faire. Ici, DriveInfo ne gère PAS les chemins
        // réseau (\\serveur\partage). On appelle donc directement une fonction
        // de Windows (kernel32.dll) : c'est le "P/Invoke". La signature décrit
        // à .NET comment appeler la fonction native (types, sens des paramètres).
        // GetDiskFreeSpaceEx renvoie l'espace libre POUR L'UTILISATEUR COURANT
        // (il tient compte des quotas de disque), ce qui est exactement ce
        // qu'on veut savoir avant de copier.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        /// <summary>
        /// Espace libre (en octets) du disque qui contient "path".
        /// Le dossier peut ne pas exister encore (destination pas encore créée) :
        /// on remonte jusqu'au premier dossier parent existant.
        /// Retourne -1 si l'information est indisponible.
        /// </summary>
        public static long GetFreeSpace(string path)
        {
            try
            {
                if (!TryNormalizePath(path, out var dir)) return -1;

                while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    dir = Path.GetDirectoryName(dir); // null une fois arrivé à la racine

                if (string.IsNullOrEmpty(dir)) return -1;

                // Windows exige un "\" final pour les chemins réseau (et l'accepte partout).
                if (!dir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    dir += Path.DirectorySeparatorChar;

                ulong freeForUser, total, totalFree;
                if (!GetDiskFreeSpaceEx(dir, out freeForUser, out total, out totalFree))
                    return -1;

                return freeForUser > long.MaxValue ? long.MaxValue : (long)freeForUser;
            }
            catch
            {
                return -1; // jamais d'exception : l'espace libre est une aide, pas une condition vitale
            }
        }

        /// <summary>
        /// Vrai s'il reste assez de place pour "requiredBytes" (+ marge de sécurité).
        /// Espace inconnu (-1) → vrai : on ne bloque pas sur une information absente.
        /// </summary>
        public static bool HasEnoughSpace(long requiredBytes, long freeBytes)
        {
            if (freeBytes < 0) return true;
            return freeBytes >= requiredBytes + SpaceSafetyMargin;
        }

        /// <summary>Espace qu'il MANQUE (marge de sécurité comprise), 0 si tout rentre ou si inconnu.</summary>
        public static long GetMissingSpace(long requiredBytes, long freeBytes)
        {
            if (freeBytes < 0) return 0;
            return Math.Max(0L, requiredBytes + SpaceSafetyMargin - freeBytes);
        }

        /// <summary>Formatage humain d'une taille (même convention que le reste du plugin : Go/Mo/Ko décimaux).</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes >= 1_000_000_000L)
                return $"{bytes / 1_000_000_000.0:F1} Go";
            if (bytes >= 1_000_000L)
                return $"{bytes / 1_000_000.0:F0} Mo";
            if (bytes >= 1_000L)
                return $"{bytes / 1_000.0:F0} Ko";
            return $"{bytes} o";
        }

        // ==============================================================
        // JAQUETTES / ICÔNES AUTOMATIQUES
        // ==============================================================
        /// <summary>
        /// Cherche une icône, une jaquette et un fond dans le dossier d'un jeu
        /// (racine ou sous-dossier "_artwork"). Lecture seule : l'archive n'est
        /// jamais modifiée. Ne parcourt PAS les sous-dossiers du jeu (rapide).
        /// Ne lève jamais d'exception (dossier inaccessible → aucune image).
        /// </summary>
        public static ArtworkFiles FindArtwork(string gameFolder)
        {
            var art = new ArtworkFiles();
            try
            {
                var files = new List<string>();

                // Ordre voulu : "_artwork" d'abord (prioritaire), puis la racine.
                var artDir = Path.Combine(gameFolder, ArtworkSubFolder);
                if (Directory.Exists(artDir))
                    files.AddRange(Directory.GetFiles(artDir));
                files.AddRange(Directory.GetFiles(gameFolder));

                art.IconPath = PickImage(files, IconNames, IconExtensions);
                art.CoverPath = PickImage(files, CoverNames, ImageExtensions);
                art.BackgroundPath = PickImage(files, BackgroundNames, ImageExtensions);
            }
            catch
            {
                // Dossier illisible : on renvoie ce qu'on a (au pire, rien).
            }
            return art;
        }

        // Premier fichier dont le nom (sans extension) correspond à l'un des "names"
        // (dans l'ordre de priorité) avec une extension autorisée.
        private static string PickImage(List<string> files, string[] names, string[] extensions)
        {
            foreach (var name in names)
                foreach (var ext in extensions)
                    foreach (var f in files)
                        if (Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase) &&
                            Path.GetExtension(f).Equals(ext, StringComparison.OrdinalIgnoreCase))
                            return f;
            return null;
        }

        // ==============================================================
        // CHEMINS RELATIFS (exe mémorisé)
        // ==============================================================
        /// <summary>
        /// Chemin de "fullPath" RELATIF à "root" (ex. "bin\jeu.exe"). Faux si
        /// fullPath n'est pas strictement à l'intérieur de root.
        /// On mémorise des chemins relatifs : si le dossier de destination
        /// change (autre profil, autre disque), l'exe mémorisé reste retrouvable.
        /// </summary>
        public static bool TryGetRelativePath(string root, string fullPath, out string relative)
        {
            relative = null;
            if (!TryNormalizePath(root, out var r) || !TryNormalizePath(fullPath, out var f))
                return false;
            if (!IsStrictlyUnder(f, r)) return false;

            relative = f.Substring(r.Length + 1);
            return true;
        }

        /// <summary>
        /// Opération inverse : reconstruit le chemin complet depuis root + relatif.
        /// SÉCURITÉ : refuse tout résultat qui sortirait de root (ex. "..\..\x.exe"
        /// ou chemin absolu glissé dans le JSON) → un fichier de settings modifié
        /// ne peut pas faire lancer un programme situé ailleurs.
        /// </summary>
        public static bool TryResolveUnder(string root, string relative, out string fullPath)
        {
            fullPath = null;
            try
            {
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
                if (!TryNormalizePath(root, out var r)) return false;
                if (!TryNormalizePath(Path.Combine(r, relative), out var combined)) return false;
                if (!IsStrictlyUnder(combined, r)) return false;

                fullPath = combined;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ==============================================================
        // SCÉNARIO 3 — DÉSINSTALLATION
        // ==============================================================
        /// <summary>Cherche un désinstalleur (unins*.exe), le plus superficiel d'abord.</summary>
        public static string FindUninstaller(string installDir)
        {
            try
            {
                return EnumerateFilesSafe(installDir)
                    .Where(f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                             && Path.GetFileNameWithoutExtension(f)
                                    .StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
                    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>
        /// Type d'un désinstalleur : par signature, sinon "unins000.exe" = Inno
        /// (nom standard Inno). None = on ne sait pas lui passer d'arguments
        /// silencieux → on ne l'exécute pas (suppression directe du dossier).
        /// </summary>
        public static InstallerType DetectUninstallerType(string uninstallerPath)
        {
            var type = DetectInstallerTypeBySignature(uninstallerPath);
            if (type != InstallerType.None) return type;

            var name = Path.GetFileNameWithoutExtension(uninstallerPath);
            if (name.Length > 5 &&
                name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) &&
                name.Skip(5).All(char.IsDigit))
                return InstallerType.InnoSetup;

            return InstallerType.None;
        }

        // ==============================================================
        // GARDE-FOUS DE CHEMINS (protection de l'ARCHIVE)
        // ==============================================================
        // Normalise un chemin (absolu, sans séparateur final). Retourne false
        // si le chemin est vide ou invalide (caractères interdits…).
        public static bool TryNormalizePath(string path, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                normalized = Path.GetFullPath(path.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return normalized.Length > 0;
            }
            catch { return false; }
        }

        private static bool IsStrictlyUnder(string child, string parent)
            => child.Length > parent.Length
               && child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Vrai si le DISQUE (ou le partage réseau) qui contient ce chemin est présent. Seule la racine est
        /// testée : si le disque est là mais que le dossier n'existe pas encore (destination neuve), c'est
        /// « accessible » (l'installation créera le dossier). Un disque externe débranché → faux.
        /// Ne lève jamais d'exception.
        /// </summary>
        public static bool IsDriveReachable(string path)
        {
            try
            {
                if (!TryNormalizePath(path, out var p)) return false;
                var root = Path.GetPathRoot(p);
                if (string.IsNullOrEmpty(root)) return false;
                return Directory.Exists(root);
            }
            catch { return false; }
        }

        /// <summary>Vrai si les deux chemins désignent le même dossier (casse et séparateur final ignorés).</summary>
        public static bool SamePath(string a, string b)
        {
            if (!TryNormalizePath(a, out var na) || !TryNormalizePath(b, out var nb)) return false;
            return na.Equals(nb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Vrai si a == b, ou si l'un est dans l'autre.</summary>
        public static bool PathsOverlap(string a, string b)
        {
            if (!TryNormalizePath(a, out var na) || !TryNormalizePath(b, out var nb)) return false;
            return na.Equals(nb, StringComparison.OrdinalIgnoreCase)
                || IsStrictlyUnder(na, nb) || IsStrictlyUnder(nb, na);
        }

        /// <summary>
        /// Dernier rempart avant une suppression récursive. Un dossier n'est
        /// supprimable que s'il est STRICTEMENT à l'intérieur d'une destination
        /// connue, n'est pas une racine de disque, et ne touche aucune SOURCE
        /// (ni dedans, ni égal, ni parent) : l'archive reste intacte quoi qu'il
        /// arrive (InstallDirectory corrompu, profil modifié…).
        /// </summary>
        public static bool IsSafeToDelete(
            string dir,
            IEnumerable<string> destinationRoots,
            IEnumerable<string> sourceRoots)
        {
            if (!TryNormalizePath(dir, out var d)) return false;

            var root = Path.GetPathRoot(d)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(root) || d.Equals(root, StringComparison.OrdinalIgnoreCase))
                return false; // racine de disque

            foreach (var s in sourceRoots ?? Enumerable.Empty<string>())
            {
                if (!TryNormalizePath(s, out var ns)) continue;
                if (d.Equals(ns, StringComparison.OrdinalIgnoreCase) ||
                    IsStrictlyUnder(d, ns) || IsStrictlyUnder(ns, d))
                    return false;
            }

            foreach (var r in destinationRoots ?? Enumerable.Empty<string>())
            {
                if (!TryNormalizePath(r, out var nr)) continue;
                if (IsStrictlyUnder(d, nr)) return true;
            }
            return false;
        }

        /// <summary>
        /// Supprime un dossier avec RÉESSAIS (fichiers verrouillés) en
        /// préservant les sous-dossiers de sauvegarde configurés.
        /// Retourne true si le dossier racine a été supprimé, false s'il reste
        /// des éléments (sauvegardes conservées). Lève l'exception si, après
        /// tous les essais, un fichier reste verrouillé.
        /// </summary>
        public static bool DeleteDirectoryWithRetry(
            string path,
            List<string> saveFolderNames,
            int maxRetries = 3,
            int delayMs = 2000)
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    DeleteContents(path, saveFolderNames);
                    // Si plus rien dedans (hors saves conservées), on retire le dossier racine.
                    if (!Directory.EnumerateFileSystemEntries(path).Any())
                    {
                        Directory.Delete(path);
                        return true;
                    }
                    return false;
                }
                catch (IOException) when (attempt < maxRetries - 1) { Thread.Sleep(delayMs); }
                catch (UnauthorizedAccessException) when (attempt < maxRetries - 1) { Thread.Sleep(delayMs); }
            }
            return false; // inatteignable (la dernière tentative lève), exigé par le compilateur
        }

        // Suppression récursive en sautant les dossiers de sauvegarde.
        private static void DeleteContents(string path, List<string> saveFolderNames)
        {
            foreach (var file in Directory.GetFiles(path))
            {
                // On retire les attributs "lecture seule / système" qui bloquent File.Delete.
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }

            foreach (var dir in Directory.GetDirectories(path))
            {
                var dirName = Path.GetFileName(dir);
                // On CONSERVE les dossiers dont le nom matche un motif de save.
                if (saveFolderNames != null &&
                    saveFolderNames.Any(s => dirName.Equals(s, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Jonction / lien symbolique : on retire LE LIEN, jamais sa cible
                // (sinon on viderait un dossier situé ailleurs, ex. l'archive !).
                if (IsReparsePoint(dir))
                {
                    Directory.Delete(dir, false);
                    continue;
                }

                DeleteContents(dir, saveFolderNames);
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
        }

        // Helper : taille fichier tolérante (fichier verrouillé → 0, pas d'exception).
        private static long SafeGetFileSize(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }
    }
}
