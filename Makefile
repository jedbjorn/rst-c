# Fork Makefile — super-coder convenience aliases (make dos-e / dos-enter).
# Every target is dos--prefixed; add your own targets below the include.
-include .super-coder/aliases.mk

# Friendly default: bare `make` and `make help` print the command chart, instead
# of running the first included target (dos-enter, which attaches a session).
# Lives here (not in the propagating aliases.mk) so the bare `help` name can't
# collide with a fork's own targets. dos-h points on to `make dos-help` for the
# full list.
.DEFAULT_GOAL := help
.PHONY: help
help: dos-h
