using System;

namespace FastLoading
{
    internal class GameData
    {
        internal const string PROCESS_NAME = "sekiro";
        internal const string PROCESS_TITLE = "Sekiro";
        internal const string PROCESS_DESCRIPTION = "Shadows Die Twice";
        internal const string PROCESS_EXE_VERSION = "1.6.0.0";
        internal static readonly string[] PROCESS_EXE_VERSION_SUPPORTED_LEGACY = new string[4]
        {
            "1.5.0.0",
            "1.4.0.0",
            "1.3.0.0",
            "1.2.0.0"
        };


        /**
            <float>fFrameTick determines default frame rate limit in seconds.
            0000000141161FCD | C743 18 8988883C             | mov dword ptr ds:[rbx+18],3C888889                    | fFrameTick
            0000000141161FD4 | 4C:89AB 70020000             | mov qword ptr ds:[rbx+270],r13                        |

            0000000141161694 (Version 1.2.0.0)
         */
        internal const string PATTERN_FRAMELOCK = "88 88 3C 4C 89 AB"; // first byte can can be 88/90 instead of 89 due to precision loss on floating point numbers
        internal const int PATTERN_FRAMELOCK_OFFSET = -1; // offset to byte array from found position
        internal const string PATTERN_FRAMELOCK_FUZZY = "C7 43 ?? ?? ?? ?? ?? 4C 89 AB";
        internal const int PATTERN_FRAMELOCK_FUZZY_OFFSET = 3;


        /**
            Reference pointer pFrametimeRunningSpeed to speed table entry that gets used in calculations. 
            Add or remove multiplications of 4bytes to pFrametimeRunningSpeed address to use a higher or lower <float>fFrametimeCriticalRunningSpeed from table.
            fFrametimeCriticalRunningSpeed should be roughly half the frame rate: 30 @ 60FPS limit, 50 @ 100FPS limit...
            00000001407D4F3D | F3:0F58D0                    | addss xmm2,xmm0                                       |
            00000001407D4F41 | 0FC6D2 00                    | shufps xmm2,xmm2,0                                    |
            00000001407D4F45 | 0F51C2                       | sqrtps xmm0,xmm2                                      |
            00000001407D4F48 | F3:0F5905 E8409202           | mulss xmm0,dword ptr ds:[1430F9038]                   | pFrametimeRunningSpeed->fFrametimeCriticalRunningSpeed
            00000001407D4F50 | 0F2FF8                       | comiss xmm7,xmm0                                      |

            00000001407D4E08 (Version 1.2.0.0)
         */
        internal const string PATTERN_FRAMELOCK_SPEED_FIX = "F3 0F 58 ?? 0F C6 ?? 00 0F 51 ?? F3 0F 59 ?? ?? ?? ?? ?? 0F 2F";
        internal const int PATTERN_FRAMELOCK_SPEED_FIX_OFFSET = 15;
        /**
            00000001430F7E10
            Value resolve in float table from pFrametimeRunningSpeed->fFrametimeCriticalRunningSpeed
            Hardcoded cause lazy -> if anyone knows how the table is calculated then tell me and I'll buy you a beer
         */
        private static readonly float[] PATCH_FRAMELOCK_SPEED_FIX_MATRIX = new float[]
        {
            15f,
            16f,
            16.6667f,
            18f,
            18.6875f,
            18.8516f,
            20f,
            24f,
            25f,
            27.5f,
            30f,
            32f,
            38.5f,
            40f,
            48f,
            49.5f,
            50f,
            57.2958f,
            60f,
            64f,
            66.75f,
            67f,
            78.8438f,
            80f,
            84f,
            90f,
            93.8f,
            100f,
            120f,
            127f,
            128f,
            130f,
            140f,
            144f,
            150f,
            500f
        };
        internal const float PATCH_FRAMELOCK_SPEED_FIX_DEFAULT_VALUE = 30f;
        /// <summary>
        /// Finds closest valid speed fix value for a frame rate limit.
        /// </summary>
        /// <param name="frameLimit">The set frame rate limit.</param>
        /// <returns>The value of the closest speed fix.</returns>
        internal static float FindSpeedFixForRefreshRate(int frameLimit)
        {
            float idealSpeedFix = frameLimit / 2f;
            float closestSpeedFix = PATCH_FRAMELOCK_SPEED_FIX_DEFAULT_VALUE;
            foreach (float speedFix in PATCH_FRAMELOCK_SPEED_FIX_MATRIX)
            {
                if (Math.Abs(idealSpeedFix - speedFix) < Math.Abs(idealSpeedFix - closestSpeedFix))
                    closestSpeedFix = speedFix;
            }
            return closestSpeedFix;
        }


        /**
            Reference to some value changing during loading screen.
            ?? : 00 or 01 ?
            ?? : 00 (normal value) / 01 (loading screen)
            00 00 10 E0
            ?? = EF or EC ?
            8E F4 7F 00 00
         */

        internal const string PATTERN_IS_IN_LOADING = "?? ?? 00 00 10 E0 ?? 8E F4 7F 00 00";
        //internal const string PATTERN_IS_IN_LOADING = "?? ?? 00 00 10 E0 EF 8E F4 7F 00 00 30 A6 DC 8E F4 7F 00 00 01 00 00 00 CB 04 00 80 48 B2 B1 43";
        internal static readonly int IS_IN_LOADING_TRUE = 256; // true
    }
}
