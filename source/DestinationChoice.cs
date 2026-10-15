using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveStore
{
    // ======================================================================
    // DestinationChoice — UNE destination proposée à l'utilisateur dans une fenêtre
    // (fenêtre d'installation, fenêtre « Choisir la destination »).
    //
    // PÉDAGOGIE : c'est une petite classe « pour l'affichage » : elle ne contient que ce dont
    // une liste déroulante a besoin (un libellé, un chemin, l'espace libre déjà calculé).
    // Elle est construite par le plugin, sur un thread d'arrière-plan, pour que la fenêtre
    // elle-même n'ait AUCUN accès disque à faire (voir InstallDialog.SetupSpaceInfo).
    // ======================================================================
    public class DestinationChoice
    {
        /// <summary>Nom facultatif donné dans les réglages (ex. « Vieux jeux »).</summary>
        public string Name { get; set; }

        /// <summary>Dossier de destination (racine : le jeu sera installé dans un sous-dossier).</summary>
        public string FolderPath { get; set; }

        /// <summary>Espace libre sur ce disque, en octets. -1 = inconnu (on ne bloque alors jamais).</summary>
        public long FreeBytes { get; set; } = -1;

        /// <summary>Vrai pour la destination par défaut du profil (la première de la liste).</summary>
        public bool IsDefault { get; set; }

        /// <summary>Faux si le disque (ou le partage réseau) de cette destination est absent (débranché…).</summary>
        public bool IsAvailable { get; set; } = true;

        /// <summary>« Nom (chemin) » si un nom existe, sinon le chemin seul.</summary>
        public string Label
        {
            get
            {
                string name = (Name ?? "").Trim();
                string text = name.Length > 0 ? name + " (" + FolderPath + ")" : FolderPath;
                if (IsDefault) text += " — par défaut";
                if (!IsAvailable) text += " — INACCESSIBLE";
                return text;
            }
        }

        public override string ToString() => Label;

        /// <summary>
        /// Construit la liste à partir des destinations d'un profil. La première est marquée « par défaut ».
        /// withFreeSpace = vrai : calcule l'espace libre de chaque disque (à faire HORS du thread UI : un
        /// disque réseau peut répondre lentement).
        /// </summary>
        public static List<DestinationChoice> FromEntries(IEnumerable<DestinationEntry> entries, bool withFreeSpace = true)
        {
            var list = new List<DestinationChoice>();
            foreach (var e in entries ?? Enumerable.Empty<DestinationEntry>())
            {
                if (e == null || string.IsNullOrWhiteSpace(e.FolderPath)) continue;
                list.Add(new DestinationChoice
                {
                    Name = e.Name,
                    FolderPath = e.FolderPath,
                    FreeBytes = withFreeSpace ? ArchiveScanner.GetFreeSpace(e.FolderPath) : -1,
                    IsDefault = list.Count == 0,
                    // Test de la racine du disque : à faire hors thread UI (un partage réseau peut être lent).
                    IsAvailable = withFreeSpace ? ArchiveScanner.IsDriveReachable(e.FolderPath) : true
                });
            }
            return list;
        }
    }
}
