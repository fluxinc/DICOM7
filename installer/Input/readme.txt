====================
DICOM7 Installation
====================

DICOM7 is a suite of services for converting between DICOM and HL7 formats.
This installer allows you to choose which components to install based on your specific needs.

Components:
-----------

1. DICOM to ORM Service (DICOM2ORM)
   - Queries DICOM worklist and delivers new orders as HL7 ORM (Order) messages
   - Useful for sending DICOM study information to RIS/HIS systems as orders
   - Monitors DICOM sources and generates HL7 ORM messages

2. DICOM to ORU Service (DICOM2ORU)
   - Converts DICOM C-Store datasets to HL7 ORU (Result) messages
   - Useful for sending DICOM result information to RIS/HIS systems
   - Listens for DICOM connections and generates HL7 ORU messages

3. ORM to DICOM Service (ORM2DICOM)
   - Converts HL7 ORM (Order) messages to DICOM Modality Worklist
   - Useful for making HL7 order information available to DICOM modalities
   - Listens for HL7 ORM connections and provides a DICOM Worklist service (SCP)

Configuration:
-------------
Each service has its own configuration file located at:
%ProgramData%\Flux Inc\DICOM7\[ServiceName]\config.yaml

For example:
%ProgramData%\Flux Inc\DICOM7\DICOM2ORM\config.yaml

Please configure these files according to your environment after installation.

Services:
---------
The installer will create Windows services for each component you select.
These services will be configured to start automatically.

Service names:
- DICOM7_DICOM2ORM
- DICOM7_DICOM2ORU
- DICOM7_ORM2DICOM

For more information and support, please visit:
https://fluxinc.co/

====================
