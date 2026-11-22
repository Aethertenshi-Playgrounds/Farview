namespace Farview;

/// <summary>
/// POSIX sh script streamed to the server over ssh ("sh -s"). It prints a few header lines once, then one block per second:
///   U &lt;uptime&gt;   C &lt;/proc/stat cpu line&gt;   M &lt;key&gt; &lt;kB&gt;   D &lt;df line&gt;   P &lt;rapl zone&gt; &lt;energy uJ&gt;   W &lt;name&gt; &lt;microwatts&gt;   END
/// It only reads /proc, /sys and runs df, hostname, uname, sleep. It writes nothing on the server.
/// </summary>
internal static class RemoteScript
{
    public const string Text = """
        LC_ALL=C; export LC_ALL
        echo "HOST $(hostname 2>/dev/null)"
        echo "KERNEL $(uname -sr 2>/dev/null)"
        echo "CORES $(grep -c '^processor' /proc/cpuinfo 2>/dev/null)"
        m=$(grep -m1 'model name' /proc/cpuinfo 2>/dev/null | cut -d: -f2-)
        echo "MODEL ${m# }"
        for z in /sys/class/powercap/intel-rapl:*; do
          case "${z##*/}" in
            intel-rapl:[0-9])
              if [ -r "$z/max_energy_range_uj" ] && [ -r "$z/name" ]; then
                read -r mx < "$z/max_energy_range_uj"
                read -r nm < "$z/name"
                echo "RAPLMAX ${z##*/} $mx $nm"
              fi ;;
          esac
        done
        while :; do
          read -r up _ < /proc/uptime
          echo "U $up"
          read -r cpu < /proc/stat
          echo "C $cpu"
          while read -r k v _; do
            case $k in
              MemTotal:|MemAvailable:|MemFree:|SwapTotal:|SwapFree:) echo "M $k $v" ;;
            esac
          done < /proc/meminfo
          df -P -k 2>/dev/null < /dev/null | sed -n '2,$s/^/D /p'
          for z in /sys/class/powercap/intel-rapl:*; do
            case "${z##*/}" in
              intel-rapl:[0-9])
                if [ -r "$z/energy_uj" ]; then
                  read -r e < "$z/energy_uj"
                  echo "P ${z##*/} $e"
                fi ;;
            esac
          done
          for f in /sys/class/power_supply/*/power_now; do
            if [ -r "$f" ]; then
              read -r w < "$f"
              n=${f%/power_now}
              echo "W bat-${n##*/} $w"
            fi
          done
          for f in /sys/class/hwmon/hwmon*/power1_average /sys/class/hwmon/hwmon*/power1_input; do
            if [ -r "$f" ]; then
              read -r w < "$f"
              n=${f%/*}
              hn=hwmon
              [ -r "$n/name" ] && read -r hn < "$n/name"
              echo "W $hn $w"
            fi
          done
          echo END
          sleep 1
        done
        """;
}
