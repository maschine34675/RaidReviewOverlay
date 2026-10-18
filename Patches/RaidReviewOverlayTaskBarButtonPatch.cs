using Comfort.Common;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace RaidReviewOverlay.Patches
{
    /// <summary>
    /// Puts a RAID REVIEW button into the bottom menu bar, right of HIDEOUT.
    /// The button is a clone of the hideout toggle, so it inherits the bar's
    /// look, hover cursor and animations without touching any prefab; it acts
    /// as a momentary button (silently untoggled after each press) and opens
    /// the same window as the hotkey.
    ///
    /// The bar is built once per login and only hidden in between (raids, the
    /// first-person hideout, some screens). This postfix runs on each
    /// MenuTaskBar.Awake, so a new bar gets the button with it; a setting
    /// changed while the game runs adds it through AddToCurrentBar.
    ///
    /// Raid Review can add a button of its own ("Insert Menu Item"), which
    /// opens the external browser. The plugin turns that setting off for the
    /// session before the menu is ever built; the leftover-group check below
    /// only covers the case where it was switched back on mid-session.
    ///
    /// The class name carries the mod prefix on purpose. The parameterless
    /// ModulePatch ctor builds the Harmony id from GetType().Name alone,
    /// without the namespace, so two mods patching MenuTaskBar.Awake from a
    /// class both called TaskBarButtonPatch register under one shared id and
    /// an UnpatchSelf in either would strip the other's patches. Do not
    /// shorten this name.
    /// </summary>
    internal class RaidReviewOverlayTaskBarButtonPatch : ModulePatch
    {
        private const string ButtonName = "RaidReviewOverlayTaskBarButton";
        private const string RaidReviewGroupName = "RaidReview";
        private const string Caption = "RAID REVIEW";
        private const string IconResource = "RaidReviewOverlay.task-bar-icon.png";

        private static Texture2D iconTexture;
        private static Sprite iconSprite;
        private static bool iconUnavailable;

        // The bar is built once per login - it lives under PreloaderUI, which
        // survives scene loads - and is HIDDEN, not destroyed, in raids, in the
        // first-person hideout and on some screens: MenuTaskBar.SetMenuChatBarVisibility
        // just deactivates its GameObject. A destroyed bar compares equal to
        // null through Unity's operator; a hidden one does not, so that needs
        // checking separately.
        private static MenuTaskBar currentBar;
        private static bool installPending;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.DeclaredMethod(typeof(MenuTaskBar), nameof(MenuTaskBar.Awake));
        }

        [PatchPostfix]
        private static void Postfix(MenuTaskBar __instance)
        {
            currentBar = __instance;
            installPending = false;
            install(__instance);
        }

        /// <summary>
        /// Puts the button on the current bar, for a setting changed at runtime
        /// - an address typed in F12 that turns the addon on, the button
        /// switched back on - which would otherwise wait for the next login.
        /// Only on a bar that is showing: built under a hidden one, the clone's
        /// Awake and OnEnable are deferred, its animator never takes the "off"
        /// state, and the caption check below takes the inactive bar for a
        /// vanished caption and freezes the animator for good. A hidden bar
        /// gets the button when it shows again, from
        /// <see cref="InstallIfPending"/>. Safe to call repeatedly: a bar that
        /// has the button already is left alone.
        /// </summary>
        internal static void AddToCurrentBar()
        {
            if (currentBar != null && currentBar.gameObject.activeInHierarchy)
            {
                installPending = false;
                install(currentBar);
            }
            else
            {
                installPending = true;
            }
        }

        /// <summary>Called every frame; one bool check unless a button is owed.</summary>
        internal static void InstallIfPending()
        {
            if (!installPending || currentBar == null || !currentBar.gameObject.activeInHierarchy)
                return;
            installPending = false;
            install(currentBar);
        }

        private static void install(MenuTaskBar taskBar)
        {
            try
            {
                hideRaidReviewButton();
                addButton(taskBar);
            }
            catch (Exception ex)
            {
                // A cosmetic button must never take the task bar down with it.
                if (Plugin.Log != null)
                    Plugin.Log.LogWarning("the menu bar button could not be added: " + ex);
            }
        }

        /// <summary>
        /// Raid Review clones the handbook tab into a ToggleGroup named
        /// "RaidReview" and hangs Application.OpenURL on it. If one is there
        /// anyway, hide it rather than leave the player with two buttons that
        /// claim the same thing and behave differently. Deactivating is
        /// reversible: Raid Review's own patch builds its button again the next
        /// time the bar is built - the next login - if this addon no longer
        /// suppresses its setting by then.
        /// </summary>
        private static void hideRaidReviewButton()
        {
            if (Plugin.Instance == null || !Plugin.Instance.ShowTaskBarButton || !Plugin.Instance.IsActive)
                return;

            // includeInactive: while the bar is hidden its groups are inactive
            // in the hierarchy, and Raid Review's button would otherwise be
            // missed and show up next to this one when the bar comes back.
            foreach (UnityEngine.UI.ToggleGroup group in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.ToggleGroup>(true))
            {
                if (group == null || group.name != RaidReviewGroupName || !group.gameObject.activeSelf)
                    continue;
                group.gameObject.SetActive(false);
                if (Plugin.Log != null)
                    Plugin.Log.LogInfo("hid Raid Review's own menu button; this addon's button opens the"
                        + " overlay instead. Turn off 'Menu Bar Button' here to get theirs back after the next login.");
            }
        }

        /// <summary>
        /// The embedded glyph as a texture, built once per session. Marked
        /// HideAndDontSave, or Unity destroys it on the next scene load and
        /// the button is left with a pink "missing texture" square. Mip levels
        /// are generated: the bar draws this at roughly 24 px, and filtering a
        /// single full-size level down to that is what makes an icon look
        /// ragged.
        /// </summary>
        private static Texture2D loadTexture()
        {
            if (iconTexture != null)
                return iconTexture;
            if (iconUnavailable)
                return null;

            try
            {
                using (Stream stream = typeof(RaidReviewOverlayTaskBarButtonPatch).Assembly.GetManifestResourceStream(IconResource))
                {
                    if (stream == null)
                    {
                        iconUnavailable = true;
                        return null;
                    }

                    var bytes = new byte[stream.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int step = stream.Read(bytes, read, bytes.Length - read);
                        if (step <= 0)
                            break;
                        read += step;
                    }

                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    texture.hideFlags = HideFlags.HideAndDontSave;
                    if (!texture.LoadImage(bytes))
                    {
                        UnityEngine.Object.Destroy(texture);
                        iconUnavailable = true;
                        return null;
                    }
                    texture.filterMode = FilterMode.Trilinear;
                    texture.wrapMode = TextureWrapMode.Clamp;
                    texture.Apply(true, false);

                    iconTexture = texture;
                    return iconTexture;
                }
            }
            catch (Exception ex)
            {
                iconUnavailable = true;
                if (Plugin.Log != null)
                    Plugin.Log.LogWarning("the menu button icon could not be loaded: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// A sprite scaled to the one the button already carries. This is the
        /// load-bearing part of not resizing the button: an Image reports its
        /// preferred size as <c>sprite.rect.width / sprite.pixelsPerUnit</c>,
        /// so handing it a 64 px sprite at the default 100 pixels-per-unit
        /// asks the layout for a glyph several times the size of the original -
        /// which grew the button and left the icon floating in the space that
        /// opened up. Matching the original's ratio keeps the reported size
        /// identical, whatever the source PNG measures.
        /// </summary>
        private static Sprite spriteFor(Sprite existing)
        {
            Texture2D texture = loadTexture();
            if (texture == null)
                return null;

            float pixelsPerUnit = 100f;
            if (existing != null && existing.rect.width > 0.01f && existing.pixelsPerUnit > 0.01f)
                pixelsPerUnit = texture.width * existing.pixelsPerUnit / existing.rect.width;

            if (iconSprite != null && Mathf.Abs(iconSprite.pixelsPerUnit - pixelsPerUnit) < 0.01f)
                return iconSprite;

            Sprite created = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit);
            created.hideFlags = HideFlags.HideAndDontSave;
            iconSprite = created;
            return created;
        }

        /// <summary>
        /// Puts the glyph on the cloned icon. Called again from the delayed
        /// steps: the button's animator drives this Image, and a clone that
        /// gets its state pushed a frame later can come back with the hideout
        /// glyph. Without the icon the tint at least keeps the clone from
        /// reading as a second HIDEOUT button.
        /// </summary>
        private static void applyIcon(GameObject clone)
        {
            if (clone == null)
                return;

            foreach (UnityEngine.UI.Image image in clone.GetComponentsInChildren<UnityEngine.UI.Image>(true))
            {
                if (image == null || image.gameObject.name.IndexOf("icon", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Sprite icon = spriteFor(image.sprite);
                if (icon == null)
                {
                    image.color = new Color(0.76f, 0.68f, 0.43f, image.color.a);
                    return;
                }

                if (image.sprite != icon)
                {
                    // Belt and braces next to the pixels-per-unit match in
                    // spriteFor: that handles a layout sizing itself from the
                    // sprite, this handles a rect that was simply set.
                    Vector2 size = image.rectTransform.sizeDelta;
                    image.sprite = icon;
                    image.overrideSprite = null;
                    image.type = UnityEngine.UI.Image.Type.Simple;
                    image.preserveAspect = true;
                    image.rectTransform.sizeDelta = size;
                }

                // Not tinted from here: the button's animator writes this
                // colour every frame and wins, which is why the first attempt
                // showed up plain white. The gold is baked into the PNG, and
                // white leaves it exactly as drawn while the bar's own hover
                // and selection states still modulate it.
                image.color = new Color(1f, 1f, 1f, image.color.a);
                return;
            }
        }

        private static void unlockClone(GameObject clone)
        {
            foreach (CanvasGroup group in clone.GetComponentsInChildren<CanvasGroup>(true))
            {
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;
            }
        }

        private static bool isInvisible(TextMeshProUGUI label)
        {
            return !label.gameObject.activeInHierarchy
                || !label.enabled
                || label.color.a < 0.9f
                || label.canvasRenderer.GetAlpha() < 0.9f
                || label.rectTransform.rect.width < 5f
                || label.transform.lossyScale.x < 0.01f;
        }

        /// <summary>
        /// Repairs every channel that can make the caption invisible. Measured
        /// in game on the same kind of clone: the animator's entry state
        /// deactivates the Text GameObject (animations can keyframe activity),
        /// so the SetActive re-assert is the load-bearing part.
        /// </summary>
        private static void heal(TextMeshProUGUI label)
        {
            for (Transform step = label.transform; step != null; step = step.parent)
            {
                if (!step.gameObject.activeSelf)
                    step.gameObject.SetActive(true);
                if (step.GetComponent<AnimatedToggle>() != null)
                    break;
            }
            label.enabled = true;
            if (label.color.a < 0.9f)
            {
                Color color = label.color;
                color.a = 1f;
                label.color = color;
            }
            if (label.canvasRenderer.GetAlpha() < 0.9f)
                label.canvasRenderer.SetAlpha(1f);
            if (label.text != Caption)
                label.text = Caption;
        }

        private static void addButton(MenuTaskBar taskBar)
        {
            if (Plugin.Instance == null || !Plugin.Instance.ShowTaskBarButton || !Plugin.Instance.IsActive)
                return;

            var toggles = AccessTools.Field(typeof(MenuTaskBar), "_toggleButtons").GetValue(taskBar)
                as Dictionary<EMenuType, AnimatedToggle>;
            AnimatedToggle hideout;
            if (toggles == null || !toggles.TryGetValue(EMenuType.Hideout, out hideout) || hideout == null)
                return;

            // The hideout entry is a WRAPPER (TaskBar/Tabs/Hideout) holding the
            // button AND the collect-items badges (NewInformation). The clone
            // must be a wrapper sibling under Tabs; putting it inside the
            // wrapper makes the badges - anchored to the wrapper's right edge -
            // sit on top of it.
            Transform wrapper = hideout.transform;
            while (wrapper.parent != null && wrapper.parent.name != "Tabs")
                wrapper = wrapper.parent;
            if (wrapper.parent == null)
                wrapper = hideout.transform;   // unknown build: behave like a plain sibling clone
            Transform parent = wrapper.parent;
            if (parent == null || parent.Find(ButtonName) != null)
                return;

            GameObject clone = UnityEngine.Object.Instantiate(wrapper.gameObject, parent);
            clone.name = ButtonName;
            clone.transform.SetSiblingIndex(wrapper.GetSiblingIndex() + 1);

            // The badge copies belong to the real hideout entry only.
            Transform clonedBadges = clone.transform.Find("NewInformation");
            if (clonedBadges != null)
                UnityEngine.Object.Destroy(clonedBadges.gameObject);

            // The wrapper's CanvasGroup carries the locked look (alpha 0.3, not
            // interactable). The real entries get unlocked at runtime via the
            // bar's tooltip bookkeeping, which never includes this clone - so
            // unlock it explicitly or it stays a grey ghost.
            unlockClone(clone);

            // LocalizedText lives on the same GameObject as its TMP label,
            // which is the reliable way to find the real caption among the
            // hidden counter labels the buttons also carry. Disabling it first
            // unsubscribes the locale listener cleanly, so nothing renames the
            // button back on a locale refresh.
            LocalizedText localized = clone.GetComponentInChildren<LocalizedText>(true);
            TextMeshProUGUI label = localized != null
                ? localized.GetComponent<TextMeshProUGUI>()
                : clone.GetComponentInChildren<TextMeshProUGUI>(true);
            if (localized != null)
            {
                localized.enabled = false;
                UnityEngine.Object.Destroy(localized);
            }
            if (label != null)
            {
                // The label may sit below an inactive intermediate container -
                // activating only the leaf would leave it hidden.
                for (Transform step = label.transform; step != null && step != clone.transform; step = step.parent)
                    step.gameObject.SetActive(true);
                label.enabled = true;
                label.text = Caption;
                label.SetAllDirty();
            }

            applyIcon(clone);

            HoverTooltipArea tooltip = clone.GetComponentInChildren<HoverTooltipArea>(true);
            if (tooltip != null)
                tooltip.SetMessageText("Opens Raid Review over the game", true);

            AnimatedToggle toggle = clone.GetComponentInChildren<AnimatedToggle>(true);
            if (toggle == null)
                return;

            // The caption is perfect at Awake and can be invisible in game:
            // the originals get their state pushed through the bar's own
            // toggle bookkeeping, which never includes this clone, and the
            // clone's OnEnable trigger fizzles because its animator is not
            // active yet mid-Instantiate. One frame later the animator IS
            // running: put it into the same "off" state as an unselected
            // original, then verify the caption renders - and if it still does
            // not, freeze the animator as the last resort.
            Plugin.Instance.RunDelayed(0.2f, () =>
            {
                if (toggle == null || label == null)
                    return;
                toggle.ToggleSilent(false);
                heal(label);
                unlockClone(clone);
                applyIcon(clone);
            });
            Plugin.Instance.RunDelayed(1.5f, () =>
            {
                if (toggle == null || label == null)
                    return;
                // A bar hidden in the meantime makes every caption look
                // vanished (activeInHierarchy is false all the way up). That is
                // no reason to freeze the animator for good.
                if (!clone.activeInHierarchy)
                    return;
                if (isInvisible(label))
                {
                    Animator animator = toggle.GetComponent<Animator>();
                    if (animator != null)
                        animator.enabled = false;
                    if (Plugin.Log != null)
                        Plugin.Log.LogInfo("the menu button caption kept vanishing; animator frozen.");
                }
                heal(label);
                applyIcon(clone);
            });

            // The clone's own Awake listener (ToggleSilent) stays: it keeps the
            // press animation in sync. The screen-switching listeners of the
            // original were added at runtime and are not carried by Instantiate.
            toggle.onValueChanged.AddListener(pressed =>
            {
                if (!pressed)
                    return;
                try
                {
                    Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.ButtonBottomBarClick);
                }
                catch
                {
                }
                // Momentary: back to unpressed, silently, before the window
                // opens - the bar should never show a stuck RAID REVIEW tab.
                toggle.ToggleSilent(false);
                // The bar outlives a reloaded plugin; a button left behind by
                // one must not throw into Unity's event dispatch.
                Plugin plugin = Plugin.Instance;
                if (plugin != null)
                    plugin.OpenWebInterfaceFromTaskBar();
            });
        }
    }
}
