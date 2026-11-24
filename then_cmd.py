# Renode monitor command: then_cmd <cmdfile> <index> <outfile>
# Runs line <index> of <cmdfile> as a monitor command and appends "> command" + its output to <outfile>.
# run.py uses it for --then steps: headless (-P -1) Renode shows no monitor output, and printing
# from monitor scripts hangs. Command text comes from a file so quotes in it need no escaping.
def mc_then_cmd(cmdfile, index, outfile):
    from Antmicro.Renode.UserInterface import CommandInteractionEater
    cmd = open(cmdfile).read().split("\n")[int(index)]
    eater = CommandInteractionEater()
    try:
        ok = self.Parse(cmd, eater)
        text = (eater.GetContents() or "").rstrip()
        err = (eater.GetError() or "").rstrip()
        if err:
            text = (text + "\n" if text else "") + "ERROR: " + err
    except Exception as e:
        text = "ERROR: %s" % e
    f = open(outfile, "a")
    f.write("> %s\n%s\n" % (cmd, text) if text else "> %s\n" % cmd)
    f.close()
