# Turning on the Interface Gateway

Stringline reads the sim through SimSig's **Interface Gateway**: a feed of train movements, the sim clock and (if you ask) signalling, which SimSig publishes on a network port while a sim runs. The gateway is off unless you turn it on.

## Turning it on

1. Start SimSig's Loader and pick the sim, as usual.
2. In the options for starting the sim, tick **Interface Gateway**. Leave its port at **51515** unless something else on your computer uses that port.
3. Start the sim. The gateway runs as long as the sim does.

The gateway has to be ticked **before** the sim starts. If you forgot, save the sim, close it, and start it again from the saved game with the gateway ticked.

## Payware sims

For a payware sim, SimSig checks that whoever connects to the gateway has a licence for it. Stringline asks for your **SimSig forum username and password** when you connect, and SimSig checks them online. Free sims need no login. See [Connecting](connecting.md#payware-sims).

## Firewalls

On one computer, with SimSig and Stringline side by side, nothing needs opening: Stringline connects to `localhost`. To run Stringline on a different computer from SimSig, the computer running SimSig has to let other computers reach port 51515: Windows Firewall usually asks the first time the sim opens the port, and you choose **Allow**.

If SimSig says it cannot **open a TCP port for hosting the information gateway**, something else is using port 51515 (often another sim, or a `stringline replay-server`), or a firewall or anti-virus program is blocking it. See [Troubleshooting](troubleshooting.md#simsig-cannot-open-the-gateway-port).

## What Stringline asks for

Stringline subscribes to train movements and the clock. Ticking **Also subscribe to train describer and signalling messages** in the Connect dialog adds the train describer and signalling feed: it is much busier, and Stringline does not need it to draw the graph.

Stringline only reads from the gateway, unless you turn on [assist](assist.md). Then it sends a route request or a TD interpose when you ask for one and confirm it, and nothing else.

SimSig's own description of the gateway, for the technically minded, is on the [SimSig wiki](https://www.simsig.co.uk/Wiki/Show?page=usertrack:interface_gateway). See [Further reading](further-reading.md).

Next: [connect](connecting.md).
